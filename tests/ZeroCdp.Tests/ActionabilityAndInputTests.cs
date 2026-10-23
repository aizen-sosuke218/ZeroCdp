using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Real-Chromium coverage for the P3b surface: hit-target and box stability, networkidle,
/// page keyboard/mouse, route fallback and context-level network helpers.
/// </summary>
public class ActionabilityAndInputTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-action-" + Guid.NewGuid().ToString("N"));
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
    public async Task Click_waits_for_a_moving_element_instead_of_using_a_stale_point()
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
                <html><head><title>idle</title>
                <style>
                  #moving { position:absolute; top:40px; left:0; width:120px; height:40px;
                            transform: translateX(-300px); transition: transform 400ms linear; }
                  #moving.ready { transform: translateX(80px); }
                </style></head>
                <body>
                  <button id="moving" onclick="document.title='moved-clicked'">moving</button>
                  <script>requestAnimationFrame(() => document.getElementById('moving').classList.add('ready'));</script>
                </body></html>
                """,
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(server.Url("/moving"));

        await page.Locator("#moving").ClickAsync();

        Assert.Equal("moved-clicked", await page.TitleAsync());
    }

    [Fact]
    public async Task Click_is_refused_while_an_overlay_covers_the_target()
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
                <html><head><title>covered</title>
                <style>
                  #target { position:absolute; top:40px; left:40px; width:160px; height:40px; }
                  #overlay { position:fixed; inset:0; background:rgba(0,0,0,0.01); }
                </style></head>
                <body>
                  <button id="target" onclick="document.title='clicked'">covered</button>
                  <div id="overlay"></div>
                  <script>
                    window.overlayClicks = 0;
                    document.getElementById('overlay').addEventListener('click', () => { window.overlayClicks++; });
                  </script>
                </body></html>
                """,
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        page.DefaultTimeout = TimeSpan.FromSeconds(1);
        await page.GotoAsync(server.Url("/covered"));

        await Assert.ThrowsAsync<CdpTimeoutException>(() => page.Locator("#target").ClickAsync().AsTask());
        Assert.NotEqual("clicked", await page.TitleAsync());

        // With the hit test off, the very same point clicks the overlay: the refusal above was
        // caused by the hit test, not by a missing element.
        page.CheckHitTarget = false;
        await page.Locator("#target").ClickAsync();
        EvaluationResult overlayClicks = await page.EvaluateAsync("window.overlayClicks", isolatedContext: false);
        Assert.True(overlayClicks.Value.TryGetInt64(out long clicks) && clicks == 1);

        page.CheckHitTarget = true;
        await page.EvaluateAsync("document.getElementById('overlay').remove()", isolatedContext: false);
        await page.Locator("#target").ClickAsync();
        Assert.Equal("clicked", await page.TitleAsync());
    }

    [Fact]
    public async Task Click_accepts_a_hit_on_a_child_and_inside_a_closed_shadow_root()
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
                <html><head><title>hit</title></head>
                <body>
                  <button id="outer" style="position:absolute;top:20px;left:20px;width:200px;height:44px">
                    <span id="inner">child covers the centre</span>
                  </button>
                  <div id="host" style="position:absolute;top:90px;left:20px"></div>
                  <script>
                    document.getElementById('outer').addEventListener('click', () => { document.title = 'outer'; });
                    const root = document.getElementById('host').attachShadow({ mode: 'closed' });
                    root.innerHTML = '<button>shadow</button>';
                    root.querySelector('button').addEventListener('click', () => { document.title = 'shadow'; });
                  </script>
                </body></html>
                """,
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(server.Url("/hit"));

        await page.Locator("#outer").ClickAsync();
        Assert.Equal("outer", await page.TitleAsync());

        await page.Locator("#host").Locator("button").ClickAsync();
        Assert.Equal("shadow", await page.TitleAsync());
    }

    [Fact]
    public async Task NetworkIdle_waits_for_a_request_that_starts_after_load()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = request =>
        {
            if (request.Path == "/slow")
            {
                // Keep the request in flight for longer than the 500 ms idle window.
                Thread.Sleep(300);
                return new TestHttpResponse { Body = "slow", ContentType = "text/plain" };
            }

            return new TestHttpResponse
            {
                Body = """
                    <!doctype html>
                    <html><head><title>load</title></head>
                    <body>
                      <script>
                        window.addEventListener('load', () => {
                          fetch('/slow').then(r => r.text()).then(() => { document.title = 'network-idle'; });
                        });
                      </script>
                    </body></html>
                    """,
            };
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.GotoAsync(server.Url("/idle"), WaitUntil.NetworkIdle);

        // Read the document rather than Target.getTargetInfo: the target title can lag behind a
        // document.title update under load, and the DOM is the source of truth here.
        EvaluationResult title = await page.EvaluateAsync("document.title");
        Assert.Equal("network-idle", title.Value.StringValue);
    }

    [Fact]
    public async Task Keyboard_and_mouse_devices_send_trusted_events()
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
                <html><head><title>input</title>
                <style>
                  body { height: 3000px; }
                  #field { position:absolute; top:20px; left:20px; width:240px; }
                  #button { position:absolute; top:70px; left:20px; width:120px; }
                </style></head>
                <body>
                  <input id="field">
                  <button id="button" onclick="document.title='mouse-clicked'">mouse</button>
                </body></html>
                """,
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(server.Url("/input"));

        await page.Locator("#field").ClickAsync();
        await page.Keyboard.TypeAsync("abc");
        await page.Keyboard.PressAsync("Control+A");
        await page.Keyboard.InsertTextAsync("xy");
        Assert.Equal("xy", await page.Locator("#field").InputValueAsync());

        BoundingBox box = (await page.Locator("#button").BoundingBoxAsync())!.Value;
        await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        Assert.Equal("mouse-clicked", await page.TitleAsync());

        await page.Mouse.WheelAsync(0, 400);
        await page.WaitForFunctionAsync("window.scrollY > 0");
    }

    [Fact]
    public async Task Route_fallback_reaches_the_next_registration_and_file_fulfilment_works()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        string body = """<html><body><h1 id="marker">from file</h1></body></html>""";
        string file = Path.Combine(_directory, "fallback.html");
        await File.WriteAllTextAsync(file, body, Encoding.UTF8);

        await using LocalHttpServer server = new();
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.RouteAsync("**/api/data", route => route.FulfillAsync(200, """{"from":"older"}""", "application/json"));
        await page.RouteAsync("**/api/**", route => route.FallbackAsync());
        await page.RouteAsync("**/page/fallback", route => route.FulfillFileAsync(file));

        await page.GotoAsync(server.Url("/api/data"));
        Assert.Contains("\"from\":\"older\"", await page.ContentAsync(), StringComparison.Ordinal);

        await page.GotoAsync(server.Url("/page/fallback"));
        Assert.Equal("from file", await page.Locator("#marker").InnerTextAsync());
    }

    [Fact]
    public async Task Context_helpers_apply_extra_headers_offline_and_media_emulation()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = request => new TestHttpResponse
        {
            Body = "<!doctype html><html><body>media</body></html>",
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        await using BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        await context.SetExtraHttpHeadersAsync([new CdpHeader("x-zerocdp", "yes")]);
        await page.GotoAsync(server.Url("/headers"));
        Assert.Contains(
            server.Requests,
            request => request.Path == "/headers"
                && request.Headers.TryGetValue("x-zerocdp", out string? value)
                && value == "yes");

        await page.GotoAsync(server.Url("/media"));
        await page.EmulateMediaAsync(reducedMotion: "reduce");
        EvaluationResult reduced = await page.EvaluateAsync(
            "matchMedia('(prefers-reduced-motion: reduce)').matches");
        Assert.True(reduced.Value.TryGetBoolean(out bool prefersReduced) && prefersReduced);

        await context.SetOfflineAsync(true);
        await Assert.ThrowsAsync<CdpNavigationException>(
            () => page.GotoAsync(server.Url("/headers"), timeout: TimeSpan.FromSeconds(5)).AsTask());
        await context.SetOfflineAsync(false);
    }
}
