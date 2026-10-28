using ZeroCdp.Browser;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

public class TabManagementTests
{
    [Fact]
    public async Task New_page_raises_page_events_and_is_listed()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = browser.Browser.DefaultContext;

        List<CdpPage> contextOpened = [];
        List<CdpPage> browserOpened = [];
        context.PageOpened += contextOpened.Add;
        browser.Browser.PageOpened += browserOpened.Add;

        CdpPage page = await context.NewPageAsync();

        Assert.Single(contextOpened);
        Assert.Same(page, contextOpened[0]);
        Assert.Single(browserOpened);
        Assert.Same(page, browserOpened[0]);
        Assert.Contains(page, context.Pages);
        Assert.Contains(page, browser.Browser.Pages);
        Assert.Same(context, page.Context);

        List<CdpPage> closed = [];
        context.PageClosed += closed.Add;
        await page.CloseAsync();

        Assert.True(page.IsClosed);
        Assert.Single(closed);
        Assert.Same(page, closed[0]);
        Assert.DoesNotContain(page, context.Pages);
        Assert.DoesNotContain(page, browser.Browser.Pages);
    }

    [Fact]
    public async Task Page_alias_event_mirrors_page_opened()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = browser.Browser.DefaultContext;

        int opened = 0;
        context.Page += _ => opened++;

        await context.NewPageAsync();

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Run_and_wait_for_page_returns_the_created_tab()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = browser.Browser.DefaultContext;

        CdpPage? created = null;
        CdpPage observed = await context.RunAndWaitForPageAsync(async () =>
        {
            created = await context.NewPageAsync();
        });

        Assert.Same(created, observed);
        Assert.Contains(observed, context.Pages);
    }

    [Fact]
    public async Task Wait_for_page_times_out_with_a_cdp_timeout()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        await Assert.ThrowsAsync<CdpTimeoutException>(
            () => browser.Browser.DefaultContext
                .WaitForPageAsync(timeout: TimeSpan.FromMilliseconds(200))
                .AsTask());
    }

    [Fact]
    public async Task Close_with_run_before_unload_respects_the_beforeunload_hook()
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
            <button id="activate">activate</button>
            <script>
              window.onbeforeunload = () => 'stay';
              document.getElementById('activate').onclick = () => {};
            </script>
            """);

        // A real click grants the user activation Chromium requires before it honours the hook.
        await page.Locator("#activate").ClickAsync();

        // Headless Chromium cancels the dialog instead of showing it, so the page stays open; a
        // dismissal from a DialogOpening handler has the same effect in headed mode.
        await page.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
        Assert.False(page.IsClosed);
        Assert.Contains(page, page.Context.Pages);

        // Removing the hook lets the regular close path finish.
        await page.EvaluateAsync("window.onbeforeunload = null");
        await page.CloseAsync();
        Assert.True(page.IsClosed);
        Assert.DoesNotContain(page, page.Context.Pages);

        // Without a hook the same call closes the tab; detach is asynchronous.
        CdpPage plain = await browser.Browser.NewPageAsync();
        await plain.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
        for (int attempt = 0; attempt < 100 && !plain.IsClosed; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(plain.IsClosed);
        Assert.DoesNotContain(plain, browser.Browser.Pages);
    }

    [Fact]
    public async Task Browser_close_alias_closes_every_tab()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage first = await browser.Browser.NewPageAsync();
        CdpPage second = await browser.Browser.DefaultContext.NewPageAsync();

        await browser.Browser.CloseAsync();

        Assert.True(first.IsClosed);
        Assert.True(second.IsClosed);
        Assert.Empty(browser.Browser.Pages);
    }
}
