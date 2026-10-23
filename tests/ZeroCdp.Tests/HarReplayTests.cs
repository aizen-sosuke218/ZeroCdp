using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public sealed class HarReplayTests
{
    [Fact]
    public async Task Context_and_page_har_routes_replay_without_network_and_can_fall_back()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-har-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string harPath = Path.Combine(directory, "session.har");

        try
        {
            await using LocalHttpServer server = new()
            {
                Handler = request => request.Path == "/har-replay"
                    ? new TestHttpResponse
                    {
                        Body = "<!doctype html><title>recorded</title><p>recorded body</p>",
                    }
                    : new TestHttpResponse
                    {
                        Body = "<!doctype html><title>network</title><p>network body</p>",
                    },
            };

            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
                new LaunchOptions { Headless = true });

            BrowserContext recording = await browser.Browser.NewContextAsync(
                new BrowserContextOptions { RecordHarPath = harPath });
            CdpPage recordedPage = await recording.NewPageAsync();
            await recordedPage.GotoAsync(server.Url("/har-replay"));
            await recording.CloseAsync();
            Assert.True(File.Exists(harPath));

            server.ClearRequests();

            await using (BrowserContext context = await browser.Browser.NewContextAsync())
            {
                await context.RouteFromHARAsync(harPath);
                CdpPage page = await context.NewPageAsync();
                await page.GotoAsync(server.Url("/har-replay"));

                Assert.Equal("recorded", await page.TitleAsync());
                Assert.Contains("recorded body", await page.ContentAsync(), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    server.Requests,
                    request => request.Path == "/har-replay");
            }

            server.ClearRequests();

            await using (BrowserContext context = await browser.Browser.NewContextAsync())
            {
                CdpPage page = await context.NewPageAsync();
                await page.RouteFromHARAsync(harPath);
                await page.GotoAsync(server.Url("/har-replay"));

                Assert.Equal("recorded", await page.TitleAsync());
                Assert.DoesNotContain(
                    server.Requests,
                    request => request.Path == "/har-replay");
            }

            server.ClearRequests();

            await using (BrowserContext context = await browser.Browser.NewContextAsync())
            {
                await context.RouteFromHARAsync(
                    harPath,
                    new HarReplayOptions { NotFound = HarNotFoundAction.Fallback });
                CdpPage page = await context.NewPageAsync();
                await page.GotoAsync(server.Url("/missing"));

                Assert.Equal("network", await page.TitleAsync());
                Assert.Contains(
                    server.Requests,
                    request => request.Path == "/missing");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
