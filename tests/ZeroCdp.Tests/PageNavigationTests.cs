using System.Diagnostics.CodeAnalysis;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public class PageNavigationTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => new TestHttpResponse
            {
                Body = $"<!doctype html><title>{request.Path}</title><h1 id='path'>{request.Path}</h1>",
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
    public async Task Reload_back_forward_and_history_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.GotoAsync(_server!.Url("/one"));
        await page.GotoAsync(_server.Url("/two"));
        Assert.Equal("/two", await page.Locator("#path").TextAsync());

        Assert.True(await page.GoBackAsync());
        Assert.EndsWith("/one", await page.UrlAsync(), StringComparison.Ordinal);

        Assert.True(await page.GoForwardAsync());
        Assert.EndsWith("/two", await page.UrlAsync(), StringComparison.Ordinal);

        await page.ReloadAsync();
        Assert.Equal("/two", await page.Locator("#path").TextAsync());

        NavigationHistoryResult history = await page.GetNavigationHistoryAsync();
        Assert.True(history.Entries.Count >= 2);
    }

    [Fact]
    public async Task Content_viewport_wait_for_url_and_load_state_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.GotoAsync(_server!.Url("/base"));
        Assert.Equal(_server.Url("/base"), await page.WaitForUrlAsync("**/base"));
        await page.WaitForLoadStateAsync(WaitUntil.Load);

        await page.SetContentAsync("<!doctype html><title>set</title><div id='set'>content</div>");
        Assert.Equal("content", await page.Locator("#set").TextAsync());

        await page.SetViewportSizeAsync(new ViewportSize(800, 600));
        EvaluationResult width = await page.EvaluateAsync("innerWidth");
        Assert.True(width.Value.TryGetInt64(out long actualWidth));
        Assert.Equal(800, actualWidth);

        await page.EmulateColorSchemeAsync(PreferredColorScheme.Dark);
        EvaluationResult dark = await page.EvaluateAsync("matchMedia('(prefers-color-scheme: dark)').matches");
        Assert.True(dark.Value.TryGetBoolean(out bool isDark));
        Assert.True(isDark);
    }

    [Fact]
    public async Task Pdf_and_bring_to_front_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/print"));
        await page.BringToFrontAsync();

        byte[] pdf = await page.PdfAsync();
        Assert.True(pdf.Length > 100);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
