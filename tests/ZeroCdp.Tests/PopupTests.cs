using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public sealed class PopupTests : IDisposable
{
    private readonly string _directory;

    public PopupTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-popup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Window_open_popup_is_registered_automatically()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string parent = Path.Combine(_directory, "parent.html");
        string child = Path.Combine(_directory, "child.html");
        await File.WriteAllTextAsync(
            parent,
            """
            <!doctype html>
            <title>parent</title>
            <button id="open" onclick="window.open('child.html', '_blank')">open</button>
            """);
        await File.WriteAllTextAsync(
            child,
            """
            <!doctype html>
            <title>child</title>
            <p id="child">popup body</p>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(parent).AbsoluteUri);

        int browserPages = 0;
        int contextPages = 0;
        browser.Browser.PageOpened += _ => browserPages++;
        page.Context.PageOpened += _ => contextPages++;

        CdpPage popup = await page.RunAndWaitForPopupAsync(
            () => page.Locator("#open").ClickAsync(),
            timeout: TimeSpan.FromSeconds(10));
        await popup.WaitForUrlAsync(new Uri(child).AbsoluteUri, TimeSpan.FromSeconds(10));
        await popup.WaitForLoadStateAsync(WaitUntil.Load, TimeSpan.FromSeconds(10));

        Assert.NotEqual(page.TargetId, popup.TargetId);
        Assert.Equal(page.TargetId, popup.OpenerId);
        Assert.Same(page.Context, popup.Context);
        Assert.Contains(popup, page.Context.Pages);
        Assert.Contains(popup, browser.Browser.Pages);
        Assert.Equal(1, browserPages);
        Assert.Equal(1, contextPages);
        Assert.Equal("child", await popup.TitleAsync());
        Assert.Contains("popup body", await popup.ContentAsync(), StringComparison.Ordinal);

        int closed = 0;
        browser.Browser.PageClosed += _ => closed++;
        await popup.CloseAsync();
        for (int attempt = 0; attempt < 100 && closed == 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, closed);
        Assert.DoesNotContain(popup, browser.Browser.Pages);
    }
}
