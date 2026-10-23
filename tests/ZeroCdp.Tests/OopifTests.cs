using System.Net;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Out-of-process iframe coverage. The main page is served from IPv4 loopback and the child from
/// IPv6 loopback, which Chromium treats as a different site and therefore puts in its own process
/// and target. This exercises child-session routing, synthetic node ids and coordinate mapping.
/// </summary>
public class OopifTests
{
    [Fact]
    public async Task Cross_site_iframe_locators_fill_click_and_bounding_box_are_routed()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer main = new(IPAddress.Loopback);
        await using LocalHttpServer child = new(IPAddress.IPv6Loopback);

        child.Handler = _ => new TestHttpResponse
        {
            Body = """
                <!doctype html>
                <html>
                <head><title>child frame</title></head>
                <body style="margin:0;font:16px sans-serif">
                  <h1 id="title">Out-of-process frame</h1>
                  <input id="name" aria-label="Name" style="margin:8px;padding:4px">
                  <button id="save" style="margin:8px;padding:8px 16px"
                    onclick="document.getElementById('result').textContent = document.getElementById('name').value + ' saved';">
                    Save
                  </button>
                  <div id="result"></div>
                </body>
                </html>
                """,
        };

        main.Handler = _ => new TestHttpResponse
        {
            Body = $"""
                <!doctype html>
                <html>
                <head><title>parent</title></head>
                <body style="margin:0">
                  <div id="before" style="height:40px">parent</div>
                  <iframe id="frame" src="{child.Url("/child")}"
                    style="display:block;border:7px solid #333;width:420px;height:240px"></iframe>
                </body>
                </html>
                """,
        };

        await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        await using BrowserContext context = await launched.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        await page.GotoAsync(main.Url("/"));
        await WaitForChildSessionAsync(page);

        Assert.Contains(page.ChildSessions, session => session.TargetType == "iframe");

        FrameLocator frame = page.FrameLocator("#frame");
        Assert.Equal("Out-of-process frame", await frame.Locator("#title").TextAsync());

        Locator name = frame.Locator("#name");
        await name.FillAsync("ZeroCDP");

        Locator save = frame.GetByRole(AriaRole.Button, "Save");
        BoundingBox? box = await save.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Value.X > 0, $"Expected the button inside the iframe, got x={box.Value.X}.");
        Assert.True(box.Value.Y > 40, $"Expected the iframe offset to be applied, got y={box.Value.Y}.");

        await save.ClickAsync();

        Assert.Equal(
            "ZeroCDP saved",
            await frame.Locator("#result").TextAsync());

        EvaluationResult evaluation = await frame.EvaluateAsync("document.title");
        Assert.Equal("child frame", evaluation.Value.StringValue);
    }

    [Fact]
    public async Task Nested_cross_site_iframes_compose_coordinate_transforms()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LocalHttpServer main = new(IPAddress.Loopback);
        await using LocalHttpServer child = new(IPAddress.IPv6Loopback);

        main.Handler = request => request.Path == "/inner"
            ? new TestHttpResponse
            {
                Body = """
                    <!doctype html>
                    <html><head><title>inner</title></head><body style="margin:0">
                      <button id="deep" style="margin:13px;padding:10px 18px"
                        onclick="this.textContent='deep clicked'">deep</button>
                    </body></html>
                    """,
            }
            : new TestHttpResponse
            {
                Body = $"""
                    <!doctype html>
                    <html><body style="margin:0">
                      <div style="height:33px">parent</div>
                      <iframe id="outer" src="{child.Url("/outer")}"
                        style="display:block;border:5px solid #444;width:500px;height:320px"></iframe>
                    </body></html>
                    """,
            };

        child.Handler = _ => new TestHttpResponse
        {
            Body = $"""
                <!doctype html>
                <html><head><title>outer</title></head><body style="margin:0">
                  <div style="height:21px">outer</div>
                  <iframe id="inner" src="{main.Url("/inner")}"
                    style="display:block;border:9px solid #777;width:380px;height:180px"></iframe>
                </body></html>
                """,
        };

        await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        await using BrowserContext context = await launched.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        await page.GotoAsync(main.Url("/"));
        await WaitForChildSessionAsync(page);
        for (int i = 0; i < 100 && page.ChildSessions.Count < 2; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(
            page.ChildSessions.Count >= 2,
            $"sessions: {string.Join(",", page.ChildSessions.Select(s => s.TargetType + ":" + s.TargetId))}");

        FrameLocator outer = page.FrameLocator("#outer");
        Assert.Equal("outer", (await outer.EvaluateAsync("document.title")).Value.StringValue);
        string outerHtml = (await outer.EvaluateAsync("document.body.innerHTML")).Value.StringValue ?? string.Empty;
        Assert.Contains("id=\"inner\"", outerHtml);
        Assert.Equal(1, await outer.Locator("#inner").CountAsync());

        FrameLocator inner = outer.Frame("#inner");
        Assert.Equal("inner", (await inner.EvaluateAsync("document.title")).Value.StringValue);

        Locator deep = inner.Locator("#deep");
        Assert.Equal("deep", await deep.TextAsync());

        BoundingBox? box = await deep.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Value.X > 0, $"Expected nested iframe x offset, got {box.Value.X}.");
        Assert.True(box.Value.Y > 50, $"Expected both iframe offsets, got {box.Value.Y}.");

        await deep.ClickAsync();
        Assert.Equal("deep clicked", await deep.TextAsync());
    }

    private static async Task WaitForChildSessionAsync(CdpPage page)
    {
        for (int i = 0; i < 250; i++)
        {
            if (page.ChildSessions.Count > 0)
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}
