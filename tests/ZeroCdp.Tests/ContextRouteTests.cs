using System.Diagnostics.CodeAnalysis;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class ContextRouteTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => new TestHttpResponse
            {
                Body = request.Path == "/normal" ? "normal-server" : "from-server",
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
    public async Task Context_route_covers_initial_navigation_and_yields_to_page_routes()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();

        int contextHits = 0;
        await context.RouteAsync("**/context", route =>
        {
            contextHits++;
            return route.FulfillAsync(200, "from-context", "text/html");
        });

        CdpPage page = await context.NewPageAsync(_server!.Url("/context"));
        Assert.Equal(1, contextHits);
        EvaluationResult initialBody = await page.EvaluateAsync("document.body.textContent");
        Assert.Equal("from-context", initialBody.Value.StringValue);

        int pageHits = 0;
        await page.RouteAsync("**/context", route =>
        {
            pageHits++;
            return route.FulfillAsync(200, "from-page", "text/html");
        });

        await page.GotoAsync(_server.Url("/context"));
        Assert.Equal(1, pageHits);
        EvaluationResult pageBody = await page.EvaluateAsync("document.body.textContent");
        Assert.Equal("from-page", pageBody.Value.StringValue);

        await context.UnrouteAsync();
        await page.UnrouteAsync();
        await page.GotoAsync(_server.Url("/context"));
        EvaluationResult serverBody = await page.EvaluateAsync("document.body.textContent");
        Assert.Equal("from-server", serverBody.Value.StringValue);

        await context.DisposeAsync();
    }

    [Fact]
    public async Task Context_observation_helpers_cover_existing_and_new_pages()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        int requests = 0;
        int responses = 0;
        PageRequest? lastRequest = null;
        context.Request += request =>
        {
            if (UrlGlob.IsMatch("**/context", request.Url))
            {
                requests++;
                lastRequest = request;
            }
        };
        context.Response += response =>
        {
            if (UrlGlob.IsMatch("**/context", response.Url))
            {
                responses++;
            }
        };

        await context.EnableNetworkAsync();
        Task<PageRequest> requestTask = context.WaitForRequestAsync("**/context").AsTask();
        Task<PageResponse> responseTask = context.WaitForResponseAsync("**/context").AsTask();
        await page.GotoAsync(_server!.Url("/context"));

        PageRequest waitedRequest = await requestTask;
        PageResponse waitedResponse = await responseTask;
        Assert.Equal(_server.Url("/context"), waitedRequest.Url);
        Assert.Equal(200, waitedResponse.Status);
        Assert.Equal(1, requests);
        Assert.Equal(1, responses);
        Assert.NotNull(lastRequest);

        requests = 0;
        responses = 0;
        CdpPage second = await context.NewPageAsync();
        await second.EnableNetworkAsync();
        await second.GotoAsync(_server.Url("/context"));
        Assert.Equal(1, requests);
        Assert.Equal(1, responses);

        await second.DisposeAsync();
        await context.DisposeAsync();
    }
}
