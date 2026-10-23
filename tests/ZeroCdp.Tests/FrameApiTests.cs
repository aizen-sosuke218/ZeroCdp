using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public class FrameApiTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-frames-" + Guid.NewGuid().ToString("N"));
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
    public async Task Exposes_the_frame_tree_and_frame_scoped_locators()
    {
        if (!HasBrowser())
        {
            return;
        }

        WriteRaw(
            "child.html",
            "<!doctype html><html><head><title>child title</title></head><body>" +
            "<input id=\"name\"><button id=\"go\" onclick=\"document.title='clicked'\">Go</button></body></html>");
        string outer = WriteRaw(
            "outer.html",
            "<!doctype html><html><head><title>outer</title></head><body>" +
            "<iframe id=\"frame\" name=\"named-frame\" src=\"child.html\" style=\"width:420px;height:220px\"></iframe>" +
            "</body></html>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(outer).AbsoluteUri);

        Assert.Same(page.MainFrame, page.Frames[0]);
        Assert.Equal(2, page.Frames.Count);

        Frame child = Assert.Single(page.MainFrame.ChildFrames);
        Assert.Equal("named-frame", child.Name);
        Assert.Same(page.MainFrame, child.ParentFrame);
        Assert.Same(child, page.Frame("named-frame"));
        Assert.Same(child, page.Frame(child.Url));
        Assert.False(child.IsDetached);

        Assert.Equal("child title", await child.TitleAsync());
        Assert.Contains("id=\"go\"", await child.ContentAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await child.Locator("#go").CountAsync());

        await child.FillAsync("#name", "frame-value");
        Assert.Equal("frame-value", await child.Locator("#name").InputValueAsync());
        await child.ClickAsync("#go");
        Assert.Equal("clicked", await child.TitleAsync());
    }

    [Fact]
    public async Task Navigates_and_replaces_a_child_frame_document()
    {
        if (!HasBrowser())
        {
            return;
        }

        WriteRaw(
            "first.html",
            "<!doctype html><html><head><title>first</title></head><body><p id=\"value\">one</p></body></html>");
        WriteRaw(
            "second.html",
            "<!doctype html><html><head><title>second</title></head><body><p id=\"value\">two</p></body></html>");
        string outer = WriteRaw(
            "outer.html",
            "<!doctype html><html><head><title>outer</title></head><body>" +
            "<iframe id=\"frame\" src=\"first.html\" style=\"width:420px;height:220px\"></iframe>" +
            "</body></html>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(outer).AbsoluteUri);

        Frame child = Assert.Single(page.MainFrame.ChildFrames);
        GotoResult result = await child.GotoAsync(new Uri(Path.Combine(_directory, "second.html")).AbsoluteUri);
        Assert.Equal(child.FrameId, result.FrameId);
        Assert.Equal("second", await child.TitleAsync());
        Assert.Equal("two", await child.Locator("#value").TextContentAsync());

        await child.SetContentAsync("<!doctype html><title>third</title><p id=\"value\">three</p>");
        Assert.Equal("three", await child.Locator("#value").TextContentAsync());
    }

    [Fact]
    public async Task Strict_mode_rejects_ambiguous_actions()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "strict.html",
            "<button class=\"item\" onclick=\"document.title='first'\">One</button>" +
            "<button class=\"item\" onclick=\"document.title='second'\">Two</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        CdpStrictModeException error = await Assert.ThrowsAsync<CdpStrictModeException>(
            () => page.Locator(".item").ClickAsync().AsTask());
        Assert.Equal(2, error.Matches);

        await page.Locator(".item").First().ClickAsync();
        Assert.Equal("first", await page.TitleAsync());

        page.StrictSelectors = false;
        await page.Locator(".item").ClickAsync();
        Assert.Equal("first", await page.TitleAsync());
    }

    [Fact]
    public async Task Context_defaults_and_init_scripts_apply_to_new_pages()
    {
        if (!HasBrowser())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = _ => new TestHttpResponse
        {
            Body = "<!doctype html><html><head><title>context</title></head><body><p id=\"value\">ready</p></body></html>",
        };

        await using LaunchedBrowser browser = await LaunchAsync();
        await using BrowserContext context = await browser.Browser.NewContextAsync();
        context.SetDefaultTimeout(4321);
        context.SetDefaultNavigationTimeout(5432);
        await context.AddInitScriptAsync("window.__zeroContext = 'installed'");

        CdpPage page = await context.NewPageAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(4321), page.DefaultTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(5432), page.DefaultNavigationTimeout);

        await page.GotoAsync(server.Url("/context"));
        Assert.Equal("installed", (await page.EvaluateAsync("window.__zeroContext", isolatedContext: false)).Value.StringValue);
    }

    [Fact]
    public async Task Raises_page_lifecycle_network_and_close_events()
    {
        if (!HasBrowser())
        {
            return;
        }

        await using LocalHttpServer server = new();
        server.Handler = _ => new TestHttpResponse
        {
            Body = "<!doctype html><html><head><title>events</title></head><body><p>events</p></body></html>",
        };

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();

        TaskCompletionSource<bool> domContentLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PageRequest> requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PageResponse> responded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        page.DOMContentLoaded += () => domContentLoaded.TrySetResult(true);
        page.Load += () => loaded.TrySetResult(true);
        page.Request += request => requested.TrySetResult(request);
        page.Response += response => responded.TrySetResult(response);
        page.Close += _ => closed.TrySetResult(true);

        await page.GotoAsync(server.Url("/events"));
        await domContentLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        PageRequest request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(server.Url("/events"), request.Url);
        Assert.True(request.IsNavigationRequest);
        Assert.Equal("GET", request.Method);

        PageResponse response = await responded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, response.Status);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Same(request, response.Request);
        Assert.Same(response, await request.ResponseAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(response.EncodedDataLength > 0);
        Assert.False(string.IsNullOrEmpty(response.Protocol));
        Assert.True(response.Timing.Count > 0);
        Assert.Equal(200, (await response.FinishedAsync().WaitAsync(TimeSpan.FromSeconds(10))).Status);

        await page.CloseAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(page.IsClosed);
    }

    [Fact]
    public async Task Locator_or_unions_matches_and_locator_state_helpers_work()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "or.html",
            "<p id=\"a\">Alpha</p><p id=\"b\">Beta</p><button id=\"disabled\" disabled>No</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator union = page.Locator("#a").Or(page.Locator("#b"));
        Assert.Equal(2, await union.CountAsync());
        Assert.Equal("Alpha", await union.First().TextContentAsync());
        Assert.Equal("Beta", await union.Last().TextContentAsync());
        Assert.Equal(1, await page.Locator("#a").Or(page.Locator("#a")).CountAsync());
        Assert.True(await page.Locator("#disabled").IsDisabledAsync());
        Assert.False(await page.Locator("#a").IsDisabledAsync());

        EvaluationResult ids = await union.EvaluateAllAsync("els => els.map(el => el.id)");
        Assert.Equal("[\"a\",\"b\"]", Encoding.UTF8.GetString(ids.Value.ValueJson.Span));

        PageRequest? request = null;
        page.Request += value => request = value;
        await page.GotoAsync(new Uri(file).AbsoluteUri);
        Assert.NotNull(request);
        IReadOnlyDictionary<string, string> headers = await request.HeadersAsync();
        Assert.True(headers.Count > 0);
        Assert.NotNull(await request.PostDataBufferAsync());
    }

    [Fact]
    public async Task Browser_and_context_metadata_and_page_legacy_actions_work()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "legacy.html",
            "<button id=\"go\" onclick=\"document.title='clicked'\">Go</button>" +
            "<input id=\"name\"><select id=\"choice\"><option value=\"a\">A</option><option value=\"b\">B</option></select>");

        await using LaunchedBrowser browser = await LaunchAsync();
        Assert.True(browser.Browser.IsConnected);
        Assert.False(string.IsNullOrEmpty(browser.Browser.Version));

        await using BrowserContext context = await browser.Browser.NewContextAsync(
            new BrowserContextOptions { StrictSelectors = false });
        CdpPage page = await context.NewPageAsync();
        Assert.False(page.StrictSelectors);
        Assert.Null(await page.OpenerAsync());

        await page.GotoAsync(new Uri(file).AbsoluteUri);
        await page.FillAsync("#name", "ZeroCDP");
        await page.ClickAsync("#go");
        Assert.Equal("clicked", await page.TitleAsync());
        Assert.Equal("ZeroCDP", await page.Locator("#name").InputValueAsync());
        Assert.Equal(["b"], await page.SelectOptionAsync("#choice", ["b"]));

        await context.CloseAsync();
        Assert.True(context.IsDisposed);
    }

    [Fact]
    public async Task Locator_handler_runs_before_an_action()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "handler.html",
            "<div id=\"overlay\" style=\"position:fixed;inset:0;background:black\">" +
            "<button id=\"close\" onclick=\"this.parentElement.remove()\">Close</button></div>" +
            "<button id=\"go\" onclick=\"document.title='handled'\">Go</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.AddLocatorHandlerAsync(
            page.Locator("#overlay"),
            async () => await page.Locator("#close").ClickAsync());

        await page.Locator("#go").ClickAsync();
        Assert.Equal("handled", await page.TitleAsync());
    }

    [Fact]
    public async Task Locator_tap_and_touchscreen_dispatch_touch_events()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "tap.html",
            "<button id=\"tap\" style=\"width:120px;height:40px\" " +
            "ontouchstart=\"document.title='touched'\">Tap</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#tap").TapAsync();
        await page.WaitForFunctionAsync("document.title === 'touched'");
        Assert.Equal("touched", await page.TitleAsync());

        await page.EvaluateAsync("document.title = 'reset'");
        await page.Touchscreen.TapAsync(30, 30);
        await page.WaitForFunctionAsync("document.title === 'touched'");
    }

    [Fact]
    public async Task Locator_click_force_trial_and_position_options_work()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "click-options.html",
            "<button id=\"pos\" style=\"position:absolute;left:100px;top:50px;width:200px;height:100px\" " +
            "onclick=\"document.title=event.clientX+','+event.clientY\">Pos</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator button = page.Locator("#pos");
        await button.ClickAsync(new LocatorClickOptions { Position = new Position(10, 20) });
        Assert.Equal("110,70", await page.TitleAsync());

        await page.EvaluateAsync("document.title = 'trial'");
        await button.ClickAsync(new LocatorClickOptions { Trial = true });
        Assert.Equal("trial", await page.TitleAsync());

        await button.ClickAsync(new LocatorClickOptions { Force = true });
        Assert.NotEqual("trial", await page.TitleAsync());
    }

    [Fact]
    public async Task Locator_hover_and_tap_options_support_trial_position_and_force()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "hover-tap-options.html",
            "<div id=\"hover\" style=\"position:absolute;left:100px;top:50px;width:200px;height:100px\" " +
            "onmouseover=\"document.title=event.clientX+','+event.clientY\">Hover</div>" +
            "<button id=\"tap\" style=\"position:absolute;left:100px;top:200px;width:200px;height:100px\" " +
            "ontouchstart=\"document.title='tap:'+event.touches[0].clientX+','+event.touches[0].clientY\">Tap</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator hover = page.Locator("#hover");
        await page.EvaluateAsync("document.title = 'trial'");
        await hover.HoverAsync(new LocatorHoverOptions { Trial = true });
        Assert.Equal("trial", await page.TitleAsync());

        await hover.HoverAsync(new LocatorHoverOptions { Position = new Position(10, 20) });
        Assert.Equal("110,70", await page.TitleAsync());

        await page.Mouse.MoveAsync(0, 0);
        await page.EvaluateAsync("document.title = 'trial'");
        await hover.HoverAsync(new LocatorHoverOptions { Force = true });
        Assert.NotEqual("trial", await page.TitleAsync());

        Locator tap = page.Locator("#tap");
        await page.EvaluateAsync("document.title = 'trial'");
        await tap.TapAsync(new LocatorTapOptions { Trial = true });
        Assert.Equal("trial", await page.TitleAsync());

        await tap.TapAsync(new LocatorTapOptions { Position = new Position(10, 20) });
        Assert.Equal("tap:110,220", await page.TitleAsync());

        await page.EvaluateAsync("document.title = 'trial'");
        await tap.TapAsync(new LocatorTapOptions { Force = true });
        Assert.NotEqual("trial", await page.TitleAsync());
    }

    [Fact]
    public async Task Fill_force_can_target_a_hidden_input()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "force-fill.html",
            "<input id=\"hidden\" style=\"display:none\" value=\"\">");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#hidden").FillAsync(
            "forced",
            new LocatorFillOptions { Force = true });
        Assert.Equal("forced", await page.Locator("#hidden").InputValueAsync());
    }

    [Fact]
    public async Task New_page_options_apply_emulation_and_strict_mode()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "page-options.html",
            "<button class=\"dup\" onclick=\"document.title='first'\">One</button>" +
            "<button class=\"dup\" onclick=\"document.title='second'\">Two</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            Viewport = new ViewportSize(640, 480),
            UserAgent = "ZeroCDP-Test",
            Locale = "fr-FR",
            ColorScheme = PreferredColorScheme.Dark,
            StrictSelectors = false,
        });

        await page.GotoAsync(new Uri(file).AbsoluteUri);
        EvaluationResult width = await page.EvaluateAsync("window.innerWidth");
        Assert.True(width.Value.TryGetInt64(out long innerWidth));
        Assert.Equal(640, innerWidth);
        EvaluationResult agent = await page.EvaluateAsync("navigator.userAgent");
        Assert.Equal("ZeroCDP-Test", agent.Value.StringValue);
        EvaluationResult dark = await page.EvaluateAsync("matchMedia('(prefers-color-scheme: dark)').matches");
        Assert.True(dark.Value.TryGetBoolean(out bool isDark) && isDark);

        await page.Locator(".dup").ClickAsync();
        Assert.Equal("first", await page.TitleAsync());
    }

    [Fact]
    public async Task Page_exposes_a_dedicated_worker_and_evaluates_in_it()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "worker.html",
            "<script>" +
            "window.worker = new Worker(URL.createObjectURL(new Blob([\"self.onmessage = () => {};\"], {type: 'text/javascript'})));" +
            "</script>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        TaskCompletionSource<Worker> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Worker += worker => observed.TrySetResult(worker);

        await page.GotoAsync(new Uri(file).AbsoluteUri);
        Worker worker = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkerKind.Dedicated, worker.Kind);
        Assert.Contains(worker, page.Workers);
        Assert.StartsWith("blob:", worker.Url, StringComparison.Ordinal);
        EvaluationResult result = await worker.EvaluateAsync("1 + 2");
        Assert.True(result.Value.TryGetInt64(out long value));
        Assert.Equal(3, value);
    }

    [Fact]
    public async Task Locator_highlight_uses_the_overlay_without_page_script()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "highlight.html",
            "<button id=\"target\" style=\"width:120px;height:40px\">Target</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#target").HighlightAsync();
        Assert.Equal("Target", await page.Locator("#target").TextAsync());
    }

    private static bool HasBrowser() => BrowserLocator.Find() is not null;

    private static ValueTask<LaunchedBrowser> LaunchAsync()
        => BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

    private string Write(string name, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"<!doctype html><html><head><title>start</title></head><body>{body}</body></html>");
        return path;
    }

    private string WriteRaw(string name, string html)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, html);
        return path;
    }
}
