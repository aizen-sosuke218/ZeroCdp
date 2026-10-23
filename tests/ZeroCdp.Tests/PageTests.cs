using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public class PageTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-page-" + Guid.NewGuid().ToString("N"));
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
    public async Task Navigates_and_reads_title_url_and_content()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = WritePage("hello.html", "<title>Hello ZeroCDP</title>", "<h1 id=\"main\">marker-42</h1>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        GotoResult navigation = await page.GotoAsync(new Uri(file).AbsoluteUri);

        Assert.False(string.IsNullOrEmpty(navigation.FrameId));
        Assert.False(string.IsNullOrEmpty(navigation.LoaderId));

        Assert.Equal("Hello ZeroCDP", await page.TitleAsync());
        Assert.Contains("hello.html", await page.UrlAsync(), StringComparison.Ordinal);
        Assert.Contains("marker-42", await page.ContentAsync(), StringComparison.Ordinal);
        Assert.Equal(page.MainFrameId, navigation.FrameId);
    }

    [Fact]
    public async Task Commit_returns_before_the_document_finishes_loading()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = WritePage("fast.html", "<title>Fast</title>", "<p>done</p>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        GotoResult navigation = await page.GotoAsync(new Uri(file).AbsoluteUri, WaitUntil.Commit);

        Assert.False(string.IsNullOrEmpty(navigation.LoaderId));
        await page.GotoAsync(new Uri(file).AbsoluteUri, WaitUntil.DomContentLoaded);
        Assert.Equal("Fast", await page.TitleAsync());
    }

    [Fact]
    public async Task Reports_a_rejected_navigation()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        // .invalid is reserved by RFC 2606 and can never resolve.
        CdpNavigationException failure = await Assert.ThrowsAsync<CdpNavigationException>(
            async () => await page.GotoAsync("http://nothing-here.invalid/", WaitUntil.Commit));

        Assert.Contains("nothing-here.invalid", failure.Url, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(failure.ErrorText));
    }

    [Fact]
    public async Task Two_pages_keep_independent_documents()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string first = WritePage("first.html", "<title>First</title>", "<p>first-body</p>");
        string second = WritePage("second.html", "<title>Second</title>", "<p>second-body</p>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

        CdpPage pageA = await browser.Browser.NewPageAsync();
        CdpPage pageB = await browser.Browser.NewPageAsync();

        await pageA.GotoAsync(new Uri(first).AbsoluteUri);
        await pageB.GotoAsync(new Uri(second).AbsoluteUri);

        Assert.Equal("First", await pageA.TitleAsync());
        Assert.Equal("Second", await pageB.TitleAsync());

        Assert.Contains("first-body", await pageA.ContentAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("second-body", await pageA.ContentAsync(), StringComparison.Ordinal);
        Assert.Contains("second-body", await pageB.ContentAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reuses_one_page_for_repeated_navigation()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        for (int i = 0; i < 5; i++)
        {
            string file = WritePage($"loop-{i}.html", $"<title>Loop {i}</title>", $"<p>body-{i}</p>");
            await page.GotoAsync(new Uri(file).AbsoluteUri);
            Assert.Equal($"Loop {i}", await page.TitleAsync());
        }
    }

    [Fact]
    public async Task Stores_and_reads_a_cookie()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        bool stored = await page.SetCookieAsync("session", "abc123", "https://example.com/");
        Assert.True(stored);

        CookiesResult cookies = await page.GetCookiesAsync(["https://example.com/"]);
        CdpCookie cookie = Assert.Single(cookies.Cookies, c => c.Name == "session");
        Assert.Equal("abc123", cookie.Value);
        Assert.Equal("example.com", cookie.Domain);
    }

    private string WritePage(string name, string head, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"<!doctype html><html><head>{head}</head><body>{body}</body></html>");
        return path;
    }
}
