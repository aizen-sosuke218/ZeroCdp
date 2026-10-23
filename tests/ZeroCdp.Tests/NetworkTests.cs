using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public class NetworkTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer();
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
    public async Task Route_can_fulfill_without_reaching_the_server()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        int handled = 0;
        await page.RouteAsync("**/mocked", async route =>
        {
            handled++;
            Assert.Equal("GET", route.Request.Method);
            await route.FulfillAsync(
                200,
                "<!doctype html><title>mocked</title><h1 id='heading'>synthetic</h1>",
                "text/html; charset=utf-8");
        });

        await page.GotoAsync(_server!.Url("/mocked"));
        Assert.Equal("mocked", await page.TitleAsync());
        Assert.Equal("synthetic", await page.Locator("#heading").TextAsync());
        Assert.Equal(1, handled);
        Assert.DoesNotContain(_server.Requests, request => request.Path == "/mocked");

        await page.UnrouteAsync("**/mocked");
        await page.GotoAsync(_server.Url("/hello"));
        Assert.Equal("hello", await page.TitleAsync());
        Assert.Contains(_server.Requests, request => request.Path == "/hello");
    }

    [Fact]
    public async Task Route_can_abort_a_navigation()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.RouteAsync("**/blocked", route => route.AbortAsync("Aborted"));

        await Assert.ThrowsAsync<CdpNavigationException>(
            async () => await page.GotoAsync(_server!.Url("/blocked")));

        await page.UnrouteAsync();
    }

    [Fact]
    public async Task Network_waiter_observes_a_response_and_request_event()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        ConcurrentBag<PageRequest> requests = new();
        page.Request += request => requests.Add(request);
        await page.EnableNetworkAsync();

        ValueTask<PageResponse> wait = page.WaitForResponseAsync("**/hello");
        await page.GotoAsync(_server!.Url("/hello"));
        PageResponse response = await wait;

        Assert.Equal(200, response.Status);
        Assert.Contains("/hello", response.Url, StringComparison.Ordinal);
        Assert.Contains(requests, request => request.Url.Contains("/hello", StringComparison.Ordinal));

        NetworkBody body = await page.GetResponseBodyAsync(response);
        Assert.Contains("Hello from /hello", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_can_continue_and_auto_continues_when_handler_does_not_act()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        int continued = 0;
        await page.RouteAsync("**/continued", route =>
        {
            continued++;
            return route.ContinueAsync();
        });

        await page.RouteAsync("**/auto", _ => ValueTask.CompletedTask);

        await page.GotoAsync(_server!.Url("/continued"));
        Assert.Equal("hello", await page.TitleAsync());
        Assert.Equal(1, continued);

        await page.GotoAsync(_server.Url("/auto"));
        Assert.Equal("hello", await page.TitleAsync());
        Assert.Contains(_server.Requests, request => request.Path == "/auto");

        await page.UnrouteAsync();
    }

    [Fact]
    public async Task Route_can_override_url_method_and_headers()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        _server!.Handler = request => request.Path switch
        {
            "/fetch-page" => new TestHttpResponse
            {
                Body = """
                    <!doctype html>
                    <script>
                      fetch('/original')
                        .then(r => r.text())
                        .then(t => { document.title = t; });
                      fetch('/post-only', { method: 'POST', body: 'original-body' })
                        .then(r => r.text())
                        .then(t => { document.body.dataset.post = t; });
                    </script>
                    """,
            },
            "/rewritten" => new TestHttpResponse { Body = "rewritten:" + request.Body },
            "/echo" => new TestHttpResponse { Body = "echo:" + request.Body },
            _ => new TestHttpResponse { Body = "unexpected" },
        };

        await page.RouteAsync(
            "**/original",
            route => route.ContinueAsync(
                _server.Url("/rewritten"),
                method: "POST",
                postData: "replaced-body",
                headers: [new CdpHeader("x-zerocdp-route", "yes")]));
        await page.RouteAsync(
            "**/post-only",
            route => route.ContinueAsync(_server.Url("/echo"), postData: "only-body"));

        await page.GotoAsync(_server.Url("/fetch-page"));
        await page.WaitForFunctionAsync(
            "document.title === 'rewritten:replaced-body'",
            TimeSpan.FromSeconds(10));
        await page.WaitForFunctionAsync(
            "document.body.dataset.post === 'echo:only-body'",
            TimeSpan.FromSeconds(10));
        TestHttpRequest rewritten = _server.Requests.Single(request => request.Path == "/rewritten");
        Assert.Equal("POST", rewritten.Method);
        Assert.Equal("replaced-body", rewritten.Body);
        Assert.Equal("yes", rewritten.Headers["x-zerocdp-route"]);
        TestHttpRequest postOnly = _server.Requests.Single(request => request.Path == "/echo");
        Assert.Equal("POST", postOnly.Method);
        Assert.Equal("only-body", postOnly.Body);
    }

    [Theory]
    [InlineData("*", "https://example.com/a", true)]
    [InlineData("**/api/*", "https://example.com/api/users", true)]
    [InlineData("https://example.com/*", "https://example.com/a/b", true)]
    [InlineData("https://example.com/*", "https://other.com/a", false)]
    [InlineData("**/*.png", "https://example.com/img/logo.png", true)]
    [InlineData("**/users?", "https://example.com/users/1", false)]
    public void Url_glob_matches_playwright_style_patterns(string pattern, string url, bool expected)
        => Assert.Equal(expected, UrlGlob.IsMatch(pattern, url));
}
