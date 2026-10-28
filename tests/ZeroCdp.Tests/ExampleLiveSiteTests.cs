using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// A readable end-to-end example: launch a real Chromium, navigate the live
/// <c>https://example.com</c> page and assert on the rendered document.
/// </summary>
public class ExampleLiveSiteTests
{
    private const string ExampleUrl = "https://example.com/";

    [Fact]
    public async Task Opens_example_com_in_a_real_chromium()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        if (!await IsReachableAsync(ExampleUrl))
        {
            return; // Offline CI: the example needs the public site.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        CdpPage page = await browser.Browser.NewPageAsync();

        GotoResult navigation = await page.GotoAsync(
            ExampleUrl,
            WaitUntil.Load,
            timeout: TimeSpan.FromSeconds(45));

        Assert.Contains("example.com", navigation.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Example Domain", await page.TitleAsync());
        Assert.StartsWith("https://example.com", await page.UrlAsync(), StringComparison.Ordinal);

        Locator paragraph = page.Locator("p").First();
        Assert.True(await paragraph.IsVisibleAsync());
        Assert.False(string.IsNullOrWhiteSpace(await paragraph.InnerTextAsync()));

        // The canonical page states the domain's purpose and links to IANA.
        Assert.Contains(
            "documentation examples",
            await page.ContentAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.Locator("a").CountAsync() > 0);
    }

    private static async ValueTask<bool> IsReachableAsync(string url)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
