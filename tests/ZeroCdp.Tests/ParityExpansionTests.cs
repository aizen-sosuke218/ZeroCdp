using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class ParityExpansionTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => request.Path switch
            {
                "/json" => new TestHttpResponse
                {
                    Body = "{\"ok\":true}",
                    ContentType = "application/json",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Test"] = "yes",
                    },
                },
                "/setcookie" => new TestHttpResponse
                {
                    Body = "ok",
                    ContentType = "text/plain",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Set-Cookie"] = "session=abc; Path=/",
                    },
                },
                "/page" => new TestHttpResponse
                {
                    Body = """
                        <!doctype html>
                        <title>page</title>
                        <a id="go" href="/second">go</a>
                        """,
                },
                "/second" => new TestHttpResponse
                {
                    Body = "<!doctype html><title>second</title>",
                },
                _ => new TestHttpResponse { Body = "<!doctype html><title>other</title>" },
            },
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Page_selector_helpers_and_element_handle_state_probes_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.SetContentAsync(
            """
            <!doctype html>
            <title>parity</title>
            <input id="name" value="Alice">
            <div id="box"><b id="inner">bold</b></div>
            <input id="dis" type="checkbox" disabled>
            <input id="en" type="text" value="x">
            <input id="agree" type="checkbox">
            <button id="emit">emit</button>
            <button id="tap" ontouchstart="document.title='touched'">tap</button>
            <iframe id="fr" srcdoc="<p id='inside'>child</p>"></iframe>
            """);

        Assert.Equal("Alice", await page.GetAttributeAsync("#name", "value"));
        Assert.Equal("bold", await page.InnerTextAsync("#inner"));
        Assert.Equal("bold", await page.InnerHTMLAsync("#inner"));
        Assert.Equal("Alice", await page.InputValueAsync("#name"));
        Assert.Equal("Alice", await page.TextContentAsync("#name"));
        Assert.True(await page.IsVisibleAsync("#name"));
        Assert.False(await page.IsHiddenAsync("#name"));
        Assert.True(await page.IsEnabledAsync("#en"));
        Assert.True(await page.IsDisabledAsync("#dis"));
        Assert.False(await page.IsCheckedAsync("#dis"));
        Assert.True(await page.IsEditableAsync("#en"));

        await page.FocusAsync("#name");
        await page.Expect(page.Locator("#name")).ToBeFocusedAsync();
        await page.SetCheckedAsync("#agree", true);
        Assert.True(await page.IsCheckedAsync("#agree"));
        await page.SetCheckedAsync("#agree", false);
        Assert.False(await page.IsCheckedAsync("#agree"));

        ElementHandle? inner = await page.QuerySelectorAsync("#inner");
        Assert.NotNull(inner);
        Assert.Equal("bold", await inner.InnerHTMLAsync());
        Assert.True(await inner.IsEnabledAsync());
        Assert.False(await inner.IsDisabledAsync());
        Assert.False(await inner.IsEditableAsync());
        Assert.True(await inner.DispatchEventAsync("custom-event", bubbles: true, cancelable: false));

        ElementHandle? name = await page.QuerySelectorAsync("#name");
        Assert.NotNull(name);
        await name.SelectTextAsync();
        EvaluationResult selection = await page.EvaluateAsync(
            "document.getElementById('name').selectionStart + ':' + document.getElementById('name').selectionEnd");
        Assert.Equal("0:5", selection.Value.StringValue);

        ElementHandle? enabled = await page.QuerySelectorAsync("#en");
        Assert.NotNull(enabled);
        Assert.True(await enabled.IsEditableAsync());

        ElementHandle? agree = await page.QuerySelectorAsync("#agree");
        Assert.NotNull(agree);
        await agree.CheckAsync(new LocatorCheckOptions { Force = true });
        Assert.True(await agree.IsCheckedAsync());
        await agree.SetCheckedAsync(false, new LocatorCheckOptions());
        Assert.False(await agree.IsCheckedAsync());

        ElementHandle? tap = await page.QuerySelectorAsync("#tap");
        Assert.NotNull(tap);
        await tap.TapAsync();
        await page.WaitForFunctionAsync("document.title === 'touched'");

        JSHandle innerHandle = await page.Locator("#box").EvaluateHandleAsync("el => el.firstElementChild");
        ElementHandle? childElement = await innerHandle.AsElementAsync();
        Assert.NotNull(childElement);
        Assert.Equal("bold", await childElement.TextContentAsync());
        await innerHandle.DisposeAsync();

        Frame? child = null;
        for (int i = 0; i < 100 && child is null; i++)
        {
            child = page.Frames.FirstOrDefault(frame => !ReferenceEquals(frame, page.MainFrame));
            if (child is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(child);
        await child.WaitForSelectorAsync("#inside");
        Assert.Equal("inside", await child.GetAttributeAsync("#inside", "id"));
        Assert.Equal("child", await child.InnerTextAsync("#inside"));
        Assert.True(await child.IsVisibleAsync("#inside"));
        await child.Expect("#inside").ToBeVisibleAsync(new ExpectOptions { Timeout = TimeSpan.FromSeconds(5) });

        ElementHandle? owner = await child.FrameElementAsync();
        Assert.NotNull(owner);
        Assert.Equal("fr", await owner.GetAttributeAsync("id"));
        Frame? viaContent = await owner.ContentFrameAsync();
        Assert.Same(child, viaContent);

        Assert.Null(await page.MainFrame.FrameElementAsync());
    }

    [Fact]
    public async Task Viewport_size_tracks_context_option_and_override()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync(
            new BrowserContextOptions { Viewport = new ViewportSize(800, 600) });
        CdpPage page = await context.NewPageAsync();
        Assert.Equal(800, page.ViewportSize?.Width);
        Assert.Equal(600, page.ViewportSize?.Height);

        await page.SetViewportSizeAsync(new ViewportSize(1024, 768));
        Assert.Equal(1024, page.ViewportSize?.Width);
        Assert.Equal(768, page.ViewportSize?.Height);

        CdpPage defaultPage = await browser.Browser.NewPageAsync();
        Assert.Null(defaultPage.ViewportSize);
    }

    [Fact]
    public async Task Navigation_and_finished_and_failed_waiters_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/page"));

        ValueTask<string> committed = page.WaitForNavigationAsync(
            "**/second",
            WaitUntil.Commit,
            TimeSpan.FromSeconds(10));
        await page.Locator("#go").ClickAsync();
        Assert.EndsWith("/second", await committed, StringComparison.Ordinal);

        string navigated = await page.RunAndWaitForNavigationAsync(
            async () => await page.EvaluateAsync("location.href = '/page'"),
            "**/page",
            WaitUntil.Load,
            TimeSpan.FromSeconds(10));
        Assert.EndsWith("/page", navigated, StringComparison.Ordinal);

        PageResponse? observed = null;
        page.RequestFinished += response =>
        {
            if (response.Url.EndsWith("/json", StringComparison.Ordinal))
            {
                observed = response;
            }
        };

        PageResponse finished = await page.RunAndWaitForRequestFinishedAsync(
            async () => await page.EvaluateAsync("fetch('/json')"),
            "**/json",
            TimeSpan.FromSeconds(10));
        Assert.Equal(200, finished.Status);
        Assert.NotNull(observed);

        ValueTask<RequestFailure> failed = page.WaitForRequestFailedAsync(
            "**/refused**",
            TimeSpan.FromSeconds(10));
        await page.EvaluateAsync("fetch('http://127.0.0.1:1/refused').catch(() => {})");
        RequestFailure failure = await failed;
        Assert.Contains("/refused", failure.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_cookie_helpers_events_and_unroute_all_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();
        await page.GotoAsync(_server!.Url("/page"));

        await context.AddCookiesAsync(
        [
            new CdpCookie { Name = "alpha", Value = "1", Url = _server.Url("/"), Path = "/" },
            new CdpCookie { Name = "beta", Value = "2", Url = _server.Url("/"), Path = "/" },
        ]);

        IReadOnlyList<CdpCookie> cookies = await context.CookiesAsync();
        Assert.Contains(cookies, cookie => cookie.Name == "alpha" && cookie.Value == "1");
        Assert.Contains(cookies, cookie => cookie.Name == "beta" && cookie.Value == "2");

        await context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = "alpha" });
        cookies = await context.CookiesAsync();
        Assert.DoesNotContain(cookies, cookie => cookie.Name == "alpha");
        Assert.Contains(cookies, cookie => cookie.Name == "beta");

        await context.ClearCookiesAsync();
        Assert.Empty(await context.CookiesAsync());

        await context.SetGeolocationAsync(new Geolocation(12.5, 56.25));
        await context.ClearGeolocationAsync();

        PageResponse? contextObserved = null;
        context.RequestFinished += response =>
        {
            if (response.Url.EndsWith("/json", StringComparison.Ordinal))
            {
                contextObserved = response;
            }
        };

        ValueTask<PageResponse> finished = context.WaitForRequestFinishedAsync(
            "**/json",
            TimeSpan.FromSeconds(10));
        await page.EvaluateAsync("fetch('/json')");
        PageResponse contextFinished = await finished;
        Assert.Equal(200, contextFinished.Status);
        Assert.NotNull(contextObserved);

        ValueTask<RequestFailure> failed = context.WaitForRequestFailedAsync(
            "**/refused**",
            TimeSpan.FromSeconds(10));
        await page.EvaluateAsync("fetch('http://127.0.0.1:1/refused').catch(() => {})");
        RequestFailure failure = await failed;
        Assert.Contains("/refused", failure.Url, StringComparison.Ordinal);

        await context.RouteAsync("**/payload", route => route.FulfillAsync(200, "routed"));
        await context.RouteWebSocketAsync("**/socket", _ => ValueTask.CompletedTask);
        await context.UnrouteAllAsync();
        await page.GotoAsync(_server.Url("/payload"));
        Assert.Contains(_server.Requests, request => request.Path == "/payload");
    }

    [Fact]
    public async Task Api_request_headers_and_storage_state_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using APIRequestContext api = await APIRequest.NewContextAsync();
        APIResponse response = await api.GetAsync(_server!.Url("/json"));
        IReadOnlyDictionary<string, string> headers = await response.HeadersAsync();
        Assert.Contains(headers.Keys, name => name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<CdpHeader> headersArray = await response.HeadersArrayAsync();
        Assert.Contains(
            headersArray,
            header => header.Name.Equals("X-Test", StringComparison.OrdinalIgnoreCase) && header.Value == "yes");

        StorageState emptyState = await api.StorageStateAsync();
        Assert.Empty(emptyState.Cookies);

        await api.GetAsync(_server.Url("/setcookie"));
        StorageState state = await api.StorageStateAsync();
        Assert.Contains(state.Cookies, cookie => cookie.Name == "session" && cookie.Value == "abc");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        await context.AddCookiesAsync(
        [
            new CdpCookie { Name = "ctx", Value = "1", Url = _server.Url("/"), Path = "/" },
        ]);

        StorageState boundState = await context.APIRequest.StorageStateAsync();
        Assert.Contains(boundState.Cookies, cookie => cookie.Name == "ctx" && cookie.Value == "1");
    }

    [Fact]
    public async Task Worker_evaluate_handle_returns_a_session_bound_handle()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        TaskCompletionSource<Worker> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Worker += worker => observed.TrySetResult(worker);

        await page.SetContentAsync(
            """
            <!doctype html>
            <script>
              window.worker = new Worker(URL.createObjectURL(
                new Blob(['self.onmessage = () => {};'], { type: 'text/javascript' })));
            </script>
            """);

        Worker worker = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        JSHandle handle = await worker.EvaluateHandleAsync("({ value: 7, label: 'w' })");
        JSHandle valueHandle = await handle.GetPropertyAsync("value");
        JsonElement? value = await valueHandle.JsonValueAsync();
        Assert.NotNull(value);
        Assert.Equal(7, value.Value.GetInt32());

        EvaluationResult sum = await handle.EvaluateAsync("self => self.value + 1");
        Assert.True(sum.Value.TryGetInt64(out long answer));
        Assert.Equal(8, answer);

        await valueHandle.DisposeAsync();
        await handle.DisposeAsync();
        Assert.True(handle.IsDisposed);
    }
}
