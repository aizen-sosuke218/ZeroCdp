using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public class LocatorTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-loc-" + Guid.NewGuid().ToString("N"));
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
    public async Task Clicks_a_button_and_the_page_reacts()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("click.html", "<button id=\"go\" onclick=\"document.title='clicked'\">Go</button>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator button = page.Locator("#go");
        Assert.Equal(1, await button.CountAsync());

        await button.ClickAsync();

        Assert.Equal("clicked", await WaitForTitleAsync(page, "clicked"));
    }

    [Fact]
    public async Task Types_with_real_key_events()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        // The handler only fires for genuine input events, so a script-injected value would not
        // update the title.
        string file = Write("type.html", "<input id=\"field\" oninput=\"document.title='got:'+this.value\">");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#field").TypeAsync("abc");

        Assert.Equal("got:abc", await WaitForTitleAsync(page, "got:abc"));
    }

    [Fact]
    public async Task Finds_and_clicks_inside_a_closed_shadow_root()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write(
            "shadow.html",
            """
            <div id="host"></div>
            <script>
              const root = document.getElementById('host').attachShadow({ mode: 'closed' });
              root.innerHTML = '<button id="inner">shadow</button>';
              root.getElementById('inner').addEventListener('click', () => { document.title = 'shadow-clicked'; });
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        // Patchright-style locators transparently fall back to a pierced snapshot when the normal
        // query finds nothing, so a closed root is reachable without an opt-in flag.
        Assert.Equal(1, await page.Locator("#inner").CountAsync());

        Locator inner = page.Locator("#inner", pierceShadowDom: true);
        Assert.Equal(1, await inner.CountAsync());

        await inner.ClickAsync();

        Assert.Equal("shadow-clicked", await WaitForTitleAsync(page, "shadow-clicked"));
    }

    [Fact]
    public async Task Clicks_an_element_below_the_fold()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write(
            "scroll.html",
            "<div style=\"height:4000px\">tall</div><button id=\"bottom\" onclick=\"document.title='bottom-clicked'\">Bottom</button>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#bottom").ScrollIntoViewAsync();
        await page.Locator("#bottom").ClickAsync();

        Assert.Equal("bottom-clicked", await WaitForTitleAsync(page, "bottom-clicked"));
    }

    [Fact]
    public async Task Reports_a_missing_element()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("empty.html", "<p>nothing here</p>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator missing = page.Locator("#does-not-exist", timeout: TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, await missing.CountAsync());
        await Assert.ThrowsAsync<CdpTimeoutException>(() => missing.ClickAsync().AsTask());
    }

    [Fact]
    public async Task Reads_element_text()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("text.html", "<p id=\"t\">alpha <b>beta</b> gamma</p><p id=\"hidden\" style=\"display:none\">hidden text</p>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Assert.Equal("alpha beta gamma", await page.Locator("#t").TextAsync());
        Assert.Equal("hidden text", await page.Locator("#hidden").TextAsync());
    }

    private static async Task<string> WaitForTitleAsync(CdpPage page, string expected, int attempts = 100)
    {
        string title = string.Empty;
        for (int i = 0; i < attempts; i++)
        {
            title = await page.TitleAsync();
            if (title == expected)
            {
                return title;
            }

            await Task.Delay(20);
        }

        return title;
    }

    private string Write(string name, string html)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"<!doctype html><html><head><title>start</title></head><body>{html}</body></html>");
        return path;
    }
}
