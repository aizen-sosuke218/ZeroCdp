using System.IO.Pipelines;
using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Security;
using ZeroCdp.Transport;
using Xunit;

namespace ZeroCdp.Tests;

public class SecurityTests
{
    [Fact]
    public void Redacts_credentials_at_any_depth_and_keeps_benign_fields()
    {
        byte[] frame =
            """{"id":1,"method":"Network.setExtraHTTPHeaders","params":{"headers":{"Authorization":"Bearer abc","Cookie":"sid=1","X-Token":"t","Accept":"text/html"},"url":"https://example.com/","password":"hunter2"}}"""u8
                .ToArray();

        string redacted = LogRedactor.RedactFrame(frame);

        Assert.DoesNotContain("Bearer abc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sid=1", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("\"***\"", redacted, StringComparison.Ordinal);
        Assert.Contains("text/html", redacted, StringComparison.Ordinal);
        Assert.Contains("https://example.com/", redacted, StringComparison.Ordinal);
        Assert.Contains("Network.setExtraHTTPHeaders", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_cookie_and_input_text_payloads()
    {
        string cookie = LogRedactor.RedactFrame(
            """{"id":2,"method":"Network.setCookie","params":{"name":"session","value":"secret-value","url":"https://example.com/"}}"""u8);
        Assert.DoesNotContain("secret-value", cookie, StringComparison.Ordinal);
        Assert.Contains("session", cookie, StringComparison.Ordinal);

        string typing = LogRedactor.RedactFrame(
            """{"id":3,"method":"Input.insertText","params":{"text":"ignored","value":"password123"}}"""u8);
        Assert.DoesNotContain("password123", typing, StringComparison.Ordinal);
        Assert.Contains("ignored", typing, StringComparison.Ordinal);

        string dialog = LogRedactor.RedactFrame(
            """{"id":4,"method":"Page.handleJavaScriptDialog","params":{"accept":true,"promptText":"hunter2"}}"""u8);
        Assert.DoesNotContain("hunter2", dialog, StringComparison.Ordinal);
        Assert.Contains("\"***\"", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_nested_arrays_and_handles_escapes()
    {
        string redacted = LogRedactor.RedactJson(
            """{"cookies":[{"name":"a","value":"b"}],"tokens":["x","y"],"note":"a \"quoted\" value"}"""u8);

        Assert.DoesNotContain("\"a\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", redacted, StringComparison.Ordinal);
        Assert.Contains("a \\\"quoted\\\" value", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_returns_malformed_json_raw()
    {
        string redacted = LogRedactor.RedactJson("""{"password":"secret"""u8);
        Assert.Equal("<invalid-json>", redacted);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);

        string frame = LogRedactor.RedactFrame("not json at all"u8);
        Assert.Equal("<invalid-cdp-frame>", frame);
    }

    [Fact]
    public void Navigation_policy_is_an_origin_allow_list()
    {
        NavigationPolicy policy = new("https://example.com", "http://localhost:8080", "file://");

        Assert.True(policy.IsAllowed(new Uri("https://example.com/path?q=1")));
        Assert.True(policy.IsAllowed(new Uri("https://EXAMPLE.com/")));
        Assert.True(policy.IsAllowed(new Uri("http://localhost:8080/app")));
        Assert.True(policy.IsAllowed(new Uri("file:///tmp/page.html")));
        Assert.True(policy.IsAllowed(new Uri("about:blank")));

        Assert.False(policy.IsAllowed(new Uri("https://example.com.evil.test/")));
        Assert.False(policy.IsAllowed(new Uri("http://example.com/")));
        Assert.False(policy.IsAllowed(new Uri("data:text/html,hello")));

        Assert.Throws<NavigationBlockedException>(() => policy.Validate("https://evil.test/"));
    }

    [Fact]
    public void Navigation_policy_rejects_wildcards_and_relative_urls()
    {
        Assert.Throws<ArgumentException>(() => new NavigationPolicy("https://*.example.com"));
        Assert.Throws<ArgumentException>(() => new NavigationPolicy("example.com"));
        Assert.Throws<NavigationBlockedException>(() => new NavigationPolicy("https://example.com").Validate("/relative"));
    }

    [Fact]
    public async Task Blocked_navigation_leaves_the_page_usable()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Path.Combine(Path.GetTempPath(), "zerocdp-blocked-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(file, "<!doctype html><title>blocked</title><p>should not load</p>");

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                NavigationPolicy = new NavigationPolicy("https://example.com"),
            });

            CdpPage page = await browser.Browser.NewPageAsync();

            await Assert.ThrowsAsync<NavigationBlockedException>(async () =>
                await page.GotoAsync(new Uri(file).AbsoluteUri));

            // The blocked URL never reached Page.navigate, so the page is still on about:blank
            // and remains usable for allowed work.
            Assert.Equal("about:blank", await page.UrlAsync());
            EvaluationResult sum = await page.EvaluateAsync("1 + 1");
            Assert.True(sum.Value.TryGetInt64(out long result));
            Assert.Equal(2, result);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Caps_the_number_of_commands_in_flight()
    {
        using SilentTransport transport = new();
        CdpConnection connection = CdpConnection.Create(
            transport,
            new CdpConnectionOptions
            {
                MaxPendingCommands = 2,
                CommandTimeout = Timeout.InfiniteTimeSpan,
            });

        ValueTask<Unit> first = default;
        ValueTask<Unit> second = default;
        try
        {
            first = connection.SendAsync<Unit>("Test.first"u8);
            second = connection.SendAsync<Unit>("Test.second"u8);

            Assert.Throws<CdpResourceLimitException>(() => SendOrThrow(connection, "Test.third"u8));
        }
        finally
        {
            await connection.DisposeAsync();
        }

        await Assert.ThrowsAsync<CdpConnectionClosedException>(async () => await first);
        await Assert.ThrowsAsync<CdpConnectionClosedException>(async () => await second);
    }

    private static void SendOrThrow(CdpConnection connection, ReadOnlySpan<byte> method)
    {
        ValueTask<Unit> task = connection.SendAsync<Unit>(method);
        task.AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Command_timeout_fails_the_command_and_clears_the_slot()
    {
        using SilentTransport transport = new();
        await using CdpConnection connection = CdpConnection.Create(
            transport,
            new CdpConnectionOptions
            {
                CommandTimeout = TimeSpan.FromMilliseconds(150),
            });

        await Assert.ThrowsAsync<CdpTimeoutException>(async () =>
            await connection.SendAsync<Unit>("Browser.getVersion"u8));

        Assert.Equal(0, connection.PendingCount);
    }

    [Fact]
    public async Task Oversized_frame_closes_the_connection_instead_of_growing_memory()
    {
        using PipeTransport transport = new();
        await using CdpConnection connection = CdpConnection.Create(
            transport,
            new CdpConnectionOptions { MaxFrameBytes = 64 });

        await transport.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 256)));

        for (int i = 0; i < 100 && !connection.IsClosed; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(connection.IsClosed);
    }

    [Fact]
    public void Private_profile_directory_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = ZeroCdp.Browser.ProfileDirectory.Create();
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class SilentTransport : ICdpTransport, IDisposable
    {
        private readonly Pipe _pipe = new();

        public PipeReader Input => _pipe.Reader;

        public string Describe => "silent";

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => default;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void Dispose()
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
        }
    }

    private sealed class PipeTransport : ICdpTransport, IDisposable
    {
        private readonly Pipe _pipe = new();

        public PipeReader Input => _pipe.Reader;

        public string Describe => "pipe-test";

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => default;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            await _pipe.Writer.WriteAsync(bytes);
            await _pipe.Writer.FlushAsync();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void Dispose()
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
        }
    }
}
