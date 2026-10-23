using System.IO.Pipelines;
using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Transport;
using Xunit;

namespace ZeroCdp.Tests;

public class SessionTests
{
    [Fact]
    public async Task Opens_several_pages_with_independent_sessions()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

        CdpPage first = await browser.Browser.NewPageAsync();
        CdpPage second = await browser.Browser.NewPageAsync();
        CdpPage third = await browser.Browser.NewPageAsync();

        Assert.Equal(3, browser.Browser.Pages.Count);

        HashSet<string> sessions = [first.Session.SessionId, second.Session.SessionId, third.Session.SessionId];
        HashSet<string> targets = [first.TargetId, second.TargetId, third.TargetId];

        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, targets.Count);

        // Target ids share a pool; session ids are per attach and must never collide.
        Assert.All(sessions, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task Keeps_page_events_on_their_own_session()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

        string directory = Path.Combine(Path.GetTempPath(), "zerocdp-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CdpPage[] pages = new CdpPage[3];
            FrameSink[] sinks = new FrameSink[3];

            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = await browser.Browser.NewPageAsync();
                sinks[i] = new FrameSink();
                pages[i].EventSink = sinks[i];

                // Page.enable is safe: unlike Runtime.enable and Console.enable it leaves no trace
                // in the page's execution context.
                await pages[i].Session.SendAsync<Unit>("Page.enable"u8);
            }

            for (int i = 0; i < pages.Length; i++)
            {
                string file = Path.Combine(directory, $"page-{i}.html");
                await File.WriteAllTextAsync(file, $"<html><head><title>page {i}</title></head><body>{i}</body></html>");

                NavigateResult navigation = await pages[i].Session
                    .SendAsync<NavigateResult, NavigateParams>(
                        "Page.navigate"u8,
                        new NavigateParams(new Uri(file).AbsoluteUri));

                Assert.Null(navigation.ErrorText);
            }

            for (int i = 0; i < pages.Length; i++)
            {
                string url = await sinks[i].WaitForNavigatedAsync(TimeSpan.FromSeconds(10));
                Assert.Contains($"page-{i}.html", url, StringComparison.Ordinal);
            }

            // Nothing leaked between sessions: each sink only ever saw its own page.
            for (int i = 0; i < pages.Length; i++)
            {
                Assert.All(sinks[i].NavigatedUrls, url => Assert.Contains($"page-{i}.html", url, StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Closing_a_page_detaches_its_session()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

        CdpPage page = await browser.Browser.NewPageAsync();
        CdpPage kept = await browser.Browser.NewPageAsync();
        string closedSession = page.Session.SessionId;

        await page.CloseAsync();

        // The detached event travels asynchronously; give it a moment without making the test flaky.
        for (int i = 0; i < 100 && browser.Browser.Sessions.Any(s => s.SessionId == closedSession); i++)
        {
            await Task.Delay(20);
        }

        Assert.DoesNotContain(browser.Browser.Sessions, s => s.SessionId == closedSession);
        Assert.Contains(browser.Browser.Sessions, s => s.SessionId == kept.Session.SessionId);
        Assert.Single(browser.Browser.Pages);
    }

    /// <summary>
    /// Routing a session-scoped event must not allocate: the session id is matched as raw UTF-8
    /// bytes, never as a <see cref="string"/>.
    /// </summary>
    [Fact]
    public async Task Routing_session_events_does_not_allocate()
    {
        using FakeTransport transport = new();
        await using CdpConnection connection = CdpConnection.Create(transport);
        SessionRouter router = new(connection);
        connection.EventSink = router;

        CountingSink sink = new();
        router.SessionAttached = session => session.EventSink = sink;

        byte[] attached = """{"sessionId":"A1B2C3D4E5","targetInfo":{"targetId":"T1","type":"page"},"waitingForDebugger":false}"""u8.ToArray();
        router.OnEvent(default, "Target.attachedToTarget"u8, attached);
        Assert.Equal(1, router.Count);

        byte[] frame = """{"method":"Network.responseReceived","sessionId":"A1B2C3D4E5","params":{"requestId":"1","type":"Document"}}"""u8.ToArray();

        for (int i = 0; i < 1_000; i++)
        {
            connection.DispatchForTest(frame);
        }

        sink.Count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++)
        {
            connection.DispatchForTest(frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(20_000, sink.Count);
        Assert.True(allocated == 0, $"Session routing allocated {allocated} bytes over 20k events.");
    }

    [Fact]
    public async Task Session_table_handles_collisions_growth_and_removal()
    {
        using FakeTransport transport = new();
        await using CdpConnection connection = CdpConnection.Create(transport);
        SessionRouter router = new(connection);

        for (int i = 0; i < 200; i++)
        {
            string json = "{\"sessionId\":\"SESSION-" + i + "\",\"targetInfo\":{\"targetId\":\"T" + i + "\",\"type\":\"page\"}}";
            router.OnEvent(default, "Target.attachedToTarget"u8, Encoding.UTF8.GetBytes(json));
        }

        Assert.Equal(200, router.Count);

        for (int i = 0; i < 200; i += 2)
        {
            string json = "{\"sessionId\":\"SESSION-" + i + "\"}";
            router.OnEvent(default, "Target.detachedFromTarget"u8, Encoding.UTF8.GetBytes(json));
        }

        Assert.Equal(100, router.Count);
        Assert.All(router.Snapshot(), session => Assert.StartsWith("SESSION-", session.SessionId, StringComparison.Ordinal));
        Assert.DoesNotContain(router.Snapshot(), session => session.SessionId == "SESSION-0");
        Assert.Contains(router.Snapshot(), session => session.SessionId == "SESSION-1");
    }

    private sealed class CountingSink : ICdpEventSink
    {
        public int Count;

        public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
            => Count++;
    }

    private sealed class FrameSink : ICdpEventSink
    {
        private readonly TaskCompletionSource<string> _firstNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _urls = new();
        private readonly Lock _gate = new();

        public IReadOnlyList<string> NavigatedUrls
        {
            get
            {
                lock (_gate)
                {
                    return _urls.ToArray();
                }
            }
        }

        public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
        {
            if (!method.SequenceEqual("Page.frameNavigated"u8))
            {
                return;
            }

            if (!TryReadUrl(paramsUtf8, out string url))
            {
                return;
            }

            lock (_gate)
            {
                _urls.Add(url);
            }

            _firstNavigation.TrySetResult(url);
        }

        public Task<string> WaitForNavigatedAsync(TimeSpan timeout) => _firstNavigation.Task.WaitAsync(timeout);

        private static bool TryReadUrl(ReadOnlySpan<byte> paramsUtf8, out string url)
        {
            url = string.Empty;
            JsonCursor cursor = new(paramsUtf8);
            if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("frame"u8) || !cursor.TryEnterObject())
            {
                return false;
            }

            if (!cursor.TrySeekProperty("url"u8) || !cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                return false;
            }

            url = Encoding.UTF8.GetString(raw);
            return true;
        }
    }

    /// <summary>A transport that discards writes, for driving the dispatcher directly.</summary>
    private sealed class FakeTransport : ICdpTransport, IDisposable
    {
        private readonly Pipe _pipe = new();

        public PipeReader Input => _pipe.Reader;

        public string Describe => "fake";

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
}
