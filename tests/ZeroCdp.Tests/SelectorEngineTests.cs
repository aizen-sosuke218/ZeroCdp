using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Covers the Playwright selector engines added alongside Patchright's closed-shadow XPath support:
/// <c>xpath=</c>, <c>text=</c>, <c>id=</c>, <c>data-testid=</c>, <c>role=</c>, plus locator filters.
/// </summary>
public class SelectorEngineTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-selector-" + Guid.NewGuid().ToString("N"));
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
    public async Task XPath_axes_predicates_and_positions_resolve()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        Assert.Equal(3, await page.Locator("//li").CountAsync());
        Assert.Equal("beta", await page.Locator("//li[2]").TextAsync());
        Assert.Equal("gamma", await page.Locator("//li[last()]").TextAsync());
        Assert.Equal("two", await page.Locator("//div[@data-kind='b']//span").TextAsync());
        Assert.Equal("gamma", await page.Locator("/html/body/ul/li[3]").TextAsync());
        Assert.Equal("x", await page.Locator("xpath=//input[@data-testid='email']").InputValueAsync());
        Assert.Equal(2, await page.Locator("//div[contains(@class, 'card')]").CountAsync());
        Assert.Equal(2, await page.Locator("//div[@data-kind and @class]").CountAsync());
    }

    [Fact]
    public async Task XPath_reaches_a_closed_shadow_root_and_respects_parent_scope()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        // Patchright supports XPath in closed shadow roots; this does it through the pierced
        // snapshot instead of injected JavaScript.
        Assert.Equal("shadow-text", await page.Locator("//span[contains(@class, 'deep')]").TextAsync());
        Assert.Equal("Shadow save", await page.Locator("#host2").Locator("//button").TextAsync());
        Assert.Equal(0, await page.Locator("#cards").Locator("//span[@class='deep']").CountAsync());
    }

    [Fact]
    public async Task Text_id_testid_and_role_engines_match_playwright_syntax()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        Assert.Equal(2, await page.Locator("text=two").CountAsync());
        Assert.Equal(1, await page.Locator("text=\"two\"").CountAsync());
        Assert.Equal(0, await page.Locator("text=\"tw\"").CountAsync());
        Assert.Equal("x", await page.Locator("id=field").InputValueAsync());
        Assert.Equal(1, await page.Locator("data-testid=email").CountAsync());
        Assert.Equal(["Toggle"], await page.Locator("role=button[name=\"Toggle\"]").AllTextContentsAsync());
        Assert.Equal("Toggle", await page.Locator("role=button[name=\"toggl\"]").TextAsync());
        Assert.Equal(1, await page.Locator("css=.card[data-kind='a']").CountAsync());
    }

    [Fact]
    public async Task Filter_and_intersection_narrow_an_existing_match_set()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        Locator cards = page.Locator(".card");
        Assert.Equal(1, await cards.Filter(new LocatorFilterOptions { HasText = "two" }).CountAsync());
        Assert.Equal(1, await cards.Filter(new LocatorFilterOptions { HasNotText = "two" }).CountAsync());
        Assert.Equal(2, await cards.Filter(new LocatorFilterOptions { Has = page.Locator("button") }).CountAsync());
        Assert.Equal(0, await cards.Filter(new LocatorFilterOptions { HasNot = page.Locator("button") }).CountAsync());
        Assert.Equal(1, await cards.Filter(new LocatorFilterOptions { Visible = true, HasText = "two" }).CountAsync());
        Assert.Equal(0, await page.Locator("#hidden-box").Filter(new LocatorFilterOptions { Visible = true }).CountAsync());

        Locator first = cards.And(page.Locator("[data-kind='a']"));
        Assert.Equal(1, await first.CountAsync());
        Assert.Equal("Save", await first.Locator("button").TextAsync());
    }

    [Fact]
    public async Task AllAsync_enumerates_each_match()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        List<string> labels = new();
        await foreach (Locator card in page.Locator(".card").AllAsync())
        {
            labels.Add(await card.Locator(".label").TextAsync());
        }

        Assert.Equal(["one", "two"], labels);
    }

    [Fact]
    public async Task Role_options_filter_by_level_and_state()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        Assert.Equal(1, await page.GetByRole("heading", new RoleOptions { Level = 2 }).CountAsync());
        Assert.Equal(0, await page.GetByRole("heading", new RoleOptions { Level = 1 }).CountAsync());
        Assert.Equal(1, await page.GetByRole("button", new RoleOptions { Checked = true }).CountAsync());
        Assert.Equal(1, await page.GetByRole("button", new RoleOptions { Name = "Toggle", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByRole("button", new RoleOptions { Name = "Toggl", Exact = true }).CountAsync());
    }

    [Fact]
    public async Task Css_pseudo_classes_has_text_and_visible_are_supported()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(WritePage()).AbsoluteUri);

        Assert.Equal(1, await page.Locator(".card:has-text('two')").CountAsync());
        Assert.Equal(1, await page.Locator("#cards:visible").CountAsync());
        Assert.Equal(0, await page.Locator("#hidden-box:visible").CountAsync());
    }

    [Fact]
    public async Task XPath_and_text_engines_work_inside_a_frame()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        string frame = Write("frame.html", """<p class="frame-paragraph">frame text</p>""");
        string file = Write(
            "host.html",
            $"""<iframe id="frame" src="{new Uri(frame).AbsoluteUri}"></iframe>""");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        FrameLocator frameLocator = page.FrameLocator("#frame");
        Assert.Equal("frame text", await frameLocator.Locator("//p[@class='frame-paragraph']").TextAsync());
        Assert.Equal(1, await frameLocator.Locator("text=frame text").CountAsync());
        Assert.Equal(1, await frameLocator.GetByRole("paragraph").CountAsync());
    }

    [Fact]
    public void Role_selector_parses_name_and_exactness()
    {
        SelectorPlan plan = SelectorPlan.Parse("role=button[name=\"Toggle\"]", pierceShadowDom: false);
        Assert.NotNull(plan.Spec);
        Assert.Equal("button", plan.Spec!.Value);
        Assert.Equal("Toggle", plan.Spec.Name);
        Assert.False(plan.Spec.Exact);
    }

    [Fact]
    public void XPath_parser_rejects_axes_outside_the_supported_subset()
    {
        Assert.Throws<SelectorSyntaxException>(() => XPathSelector.Parse("//div[following-sibling::p]"));
        Assert.Throws<SelectorSyntaxException>(() => XPathSelector.Parse("//div["));
        XPathSelector.Parse("//div[@id='a']/span[2]");
    }

    private string WritePage()
        => Write(
            "engines.html",
            """
            <div id="cards">
              <div class="card" data-kind="a"><span class="label">one</span><button>Save</button></div>
              <div class="card" data-kind="b"><span class="label">two</span><button aria-checked="true">Toggle</button></div>
            </div>
            <ul id="list"><li>alpha</li><li>beta</li><li>gamma</li></ul>
            <h2>Heading two</h2>
            <div id="hidden-box" style="display:none">secret</div>
            <input id="field" data-testid="email" value="x">
            <div id="host2"></div>
            <script>
              const root = document.getElementById('host2').attachShadow({ mode: 'closed' });
              root.innerHTML = '<span class="deep">shadow-text</span><button>Shadow save</button>';
            </script>
            """);

    private string Write(string name, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(
            path,
            $"<!doctype html><html><head><title>test</title></head><body>{body}</body></html>",
            Encoding.UTF8);
        return path;
    }
}
