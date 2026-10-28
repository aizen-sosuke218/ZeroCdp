using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Patchright-parity surface: exposed functions without <c>Runtime.enable</c>, init scripts
/// injected through the document response, and attaching to a running browser over CDP.
/// </summary>
public class PatchrightParityTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-patchright-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExposeFunction_calls_managed_code_without_enabling_runtime()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = _ => new TestHttpResponse
        {
            Body = "<!doctype html><html><head><title>fn</title></head><body><p>fn</p></body></html>",
        };

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true },
            new CdpConnectionOptions { CommandObserver = recorder.Observe });

        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(server.Url("/fn"));

        bool called = false;
        await page.ExposeFunctionAsync("addNumbers", value =>
        {
            called = true;
            string[] parts = (value ?? string.Empty).Split('+');
            return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
                + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        EvaluationResult sum = await page.EvaluateAsync("window.addNumbers('2+3')", isolatedContext: false);
        Assert.Equal("5", sum.Value.StringValue);
        Assert.True(called);

        EvaluationResult failure = await page.EvaluateAsync("window.addNumbers('x+y')", isolatedContext: false);
        Assert.True(failure.Threw);

        Assert.DoesNotContain("Runtime.enable", recorder.Methods);
        Assert.DoesNotContain("Console.enable", recorder.Methods);
    }

    [Fact]
    public async Task AddInitScript_runs_before_page_scripts_without_injection_commands()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = _ => new TestHttpResponse
        {
            Body = """
                <!doctype html>
                <html>
                <head><title>init</title><script>window.pageSawInit = window.__initRuns === 1;</script></head>
                <body><p id="marker">init</p></body>
                </html>
                """,
        };

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true },
            new CdpConnectionOptions { CommandObserver = recorder.Observe });

        CdpPage page = await browser.Browser.NewPageAsync();
        await page.AddInitScriptAsync(
            "window.__initRuns = (window.__initRuns || 0) + 1; document.documentElement.setAttribute('data-init', 'yes');");

        await page.GotoAsync(server.Url("/init"));

        string content = await page.ContentAsync();
        Assert.Contains("__initRuns", content, StringComparison.Ordinal);

        EvaluationResult sawInit = await page.EvaluateAsync("window.pageSawInit", isolatedContext: false);
        Assert.True(sawInit.Value.TryGetBoolean(out bool ranBeforeScripts) && ranBeforeScripts);

        EvaluationResult marker = await page.EvaluateAsync("document.documentElement.getAttribute('data-init')");
        Assert.Equal("yes", marker.Value.StringValue);

        // A second navigation gets a fresh document, so the script has to be injected again.
        await page.ReloadAsync();
        EvaluationResult afterReload = await page.EvaluateAsync("window.__initRuns", isolatedContext: false);
        Assert.True(afterReload.Value.TryGetInt64(out long runs) && runs == 1);

        Assert.DoesNotContain("Page.addScriptToEvaluateOnNewDocument", recorder.Methods);
        Assert.DoesNotContain("Runtime.enable", recorder.Methods);
        Assert.DoesNotContain("Console.enable", recorder.Methods);
    }

    [Fact]
    public async Task ConnectOverCdp_controls_a_browser_started_in_port_mode()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = _ => new TestHttpResponse
        {
            Body = "<!doctype html><html><head><title>attached</title></head><body><h1 id=\"marker\">attached page</h1></body></html>",
        };

        await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true, Transport = CdpTransportKind.Port });

        string endpoint = launched.WebSocketEndpoint ?? throw new InvalidOperationException("No endpoint.");
        Assert.StartsWith("ws://127.0.0.1:", endpoint, StringComparison.Ordinal);

        await using CdpBrowser attached = await BrowserLauncher.ConnectOverCDPAsync(endpoint);
        CdpPage page = await attached.NewPageAsync(server.Url("/attached"));

        Assert.Equal("attached page", await page.Locator("#marker").InnerTextAsync());

        // Disposing an attached client disconnects and does not close the remote browser.
        await page.CloseAsync();
    }

    [Fact]
    public void Proxy_launch_adds_the_webrtc_policy_flag()
    {
        LaunchOptions options = new()
        {
            Headless = true,
            ProxyServer = "http://127.0.0.1:8080",
            ProxyBypassList = "localhost",
        };

        List<string> args = LaunchArguments.Build(options, "/tmp/profile", usePipe: true);

        Assert.Contains("--proxy-server=http://127.0.0.1:8080", args);
        Assert.Contains("--proxy-bypass-list=localhost", args);
        Assert.Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp", args);

        LaunchOptions withoutProxy = new() { Headless = true };
        Assert.DoesNotContain(
            "--webrtc-ip-handling-policy=disable_non_proxied_udp",
            LaunchArguments.Build(withoutProxy, "/tmp/profile", usePipe: true));
    }

    private sealed class CommandRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<byte[]> _frames = new();

        public void Observe(ReadOnlySpan<byte> frame)
        {
            lock (_gate)
            {
                _frames.Add(frame.ToArray());
            }
        }

        public string[] Methods
        {
            get
            {
                byte[][] frames;
                lock (_gate)
                {
                    frames = _frames.ToArray();
                }

                List<string> methods = new(frames.Length);
                foreach (byte[] frame in frames)
                {
                    string text = Encoding.UTF8.GetString(frame);
                    int start = text.IndexOf("\"method\":\"", StringComparison.Ordinal);
                    if (start < 0)
                    {
                        continue;
                    }

                    start += "\"method\":\"".Length;
                    int end = text.IndexOf('"', start);
                    if (end > start)
                    {
                        methods.Add(text[start..end]);
                    }
                }

                return methods.ToArray();
            }
        }
    }
}
