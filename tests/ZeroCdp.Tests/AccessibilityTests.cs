using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public sealed class AccessibilityTests
{
    [Fact]
    public async Task Accessibility_tree_and_aria_snapshot_expose_roles_and_names()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LocalHttpServer server = new()
        {
            Handler = _ => new TestHttpResponse
            {
                Body =
                    """
                    <!doctype html>
                    <html>
                    <head><title>a11y</title></head>
                    <body>
                      <h1>Welcome</h1>
                      <button aria-label="Submit order">Go</button>
                      <input type="email" aria-label="Email">
                      <div role="checkbox" aria-checked="true" aria-label="Remember"></div>
                    </body>
                    </html>
                    """,
            },
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(server.Url("/a11y"));

        AccessibilityNode? root = await page.Accessibility.SnapshotAsync();
        Assert.NotNull(root);
        Assert.NotNull(root.Find("button", "Submit order"));
        AccessibilityNode? checkbox = root.Find("checkbox", "Remember");
        Assert.NotNull(checkbox);
        Assert.Contains(checkbox.Properties, property => property.Name == "checked" && property.Value == "true");

        string snapshot = await page.Locator("body").AriaSnapshotAsync();
        Assert.Contains("heading \"Welcome\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("button \"Submit order\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("textbox \"Email\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("checkbox \"Remember\" [checked]", snapshot, StringComparison.Ordinal);

        string heading = await page.Locator("h1").AriaSnapshotAsync();
        Assert.Contains("heading \"Welcome\"", heading, StringComparison.Ordinal);
    }
}
