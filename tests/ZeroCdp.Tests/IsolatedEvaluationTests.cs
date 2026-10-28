using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Patchright-compatible evaluation: every patched entry point runs in an isolated world by
/// default, and the main world is only reached through an explicit opt-in.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class IsolatedEvaluationTests : IAsyncLifetime
{
    private const string ChildHtml =
        """
        <!doctype html>
        <html>
        <head>
          <title>child</title>
          <script>window.mainWorldValue = 'child';</script>
        </head>
        <body>
          <div id="inner">child-dom</div>
          <button id="child-button">child</button>
        </body>
        </html>
        """;

    private const string PageHtml =
        """
        <!doctype html>
        <html>
        <head>
          <title>isolated</title>
          <script>window.mainWorldValue = 'main';</script>
        </head>
        <body>
          <h1>Title</h1>
          <div id="value">dom</div>
          <ul>
            <li class="item">one</li>
            <li class="item">two</li>
            <li class="item">three</li>
          </ul>
          <div class="card"><button class="action">inside</button></div>
          <div class="card"><button class="action">second</button></div>
          <button id="ready" data-ready="0">Ready</button>
          <iframe id="frame" src="/child" style="width:420px;height:220px"></iframe>
          <script>
            document.getElementById('ready').addEventListener('dblclick', () => {
              document.title = 'dbl';
            });
          </script>
        </body>
        </html>
        """;

    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => request.Path switch
            {
                "/isolated" => new TestHttpResponse { Body = PageHtml },
                "/child" => new TestHttpResponse { Body = ChildHtml },
                "/api" => new TestHttpResponse
                {
                    ContentType = "application/json",
                    Body = """{"from":"upstream","items":[1,2,3]}""",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["X-Upstream"] = "yes",
                    },
                },
                _ => new TestHttpResponse { Body = "<!doctype html><title>other</title>" },
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
    public async Task Page_evaluation_is_isolated_by_default()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        EvaluationResult isolated = await page.EvaluateAsync("typeof window.mainWorldValue");
        Assert.Equal("undefined", isolated.Value.StringValue);

        EvaluationResult main = await page.EvaluateAsync(
            "typeof window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("string", main.Value.StringValue);

        // Isolation must not hide the DOM.
        EvaluationResult dom = await page.EvaluateAsync(
            "document.querySelector('#value').textContent");
        Assert.Equal("dom", dom.Value.StringValue);

        // Globals created in the isolated world stay there.
        await page.EvaluateAsync("window.isolatedMarker = 'set'");
        EvaluationResult leak = await page.EvaluateAsync(
            "typeof window.isolatedMarker",
            isolatedContext: false);
        Assert.Equal("undefined", leak.Value.StringValue);
    }

    [Fact]
    public async Task Locator_evaluation_is_isolated_by_default()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        Locator value = page.Locator("#value");
        EvaluationResult isolated = await value.EvaluateAsync(
            "el => typeof window.mainWorldValue");
        Assert.Equal("undefined", isolated.Value.StringValue);

        EvaluationResult main = await value.EvaluateAsync(
            "el => typeof window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("string", main.Value.StringValue);

        EvaluationResult text = await value.EvaluateAsync("el => el.textContent");
        Assert.Equal("dom", text.Value.StringValue);
    }

    [Fact]
    public async Task Locator_evaluate_all_is_isolated_by_default()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        Locator items = page.Locator(".item");
        EvaluationResult joined = await items.EvaluateAllAsync(
            "els => els.map(el => el.textContent).join(',')");
        Assert.Equal("one,two,three", joined.Value.StringValue);

        EvaluationResult isolated = await items.EvaluateAllAsync(
            "els => typeof window.mainWorldValue");
        Assert.Equal("undefined", isolated.Value.StringValue);

        EvaluationResult main = await items.EvaluateAllAsync(
            "els => typeof window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("string", main.Value.StringValue);
    }

    [Fact]
    public async Task Element_handle_evaluation_re_resolves_in_the_requested_world()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        await using ElementHandle handle = await page.Locator("#value").ElementHandleAsync();

        EvaluationResult isolated = await handle.EvaluateAsync(
            "el => typeof window.mainWorldValue");
        Assert.Equal("undefined", isolated.Value.StringValue);

        EvaluationResult main = await handle.EvaluateAsync(
            "el => typeof window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("string", main.Value.StringValue);

        EvaluationResult text = await handle.EvaluateAsync("el => el.textContent");
        Assert.Equal("dom", text.Value.StringValue);
    }

    [Fact]
    public async Task Frame_evaluation_uses_the_frame_isolated_world()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        Frame frame = Assert.IsType<Frame>(page.FrameByUrl("**/child"));
        Assert.Same(frame, page.FrameByUrl(new Regex("/child$")));
        Assert.Same(frame, page.FrameByUrl(url => url.EndsWith("/child", StringComparison.Ordinal)));

        EvaluationResult isolated = await frame.EvaluateAsync("typeof window.mainWorldValue");
        Assert.Equal("undefined", isolated.Value.StringValue);

        EvaluationResult main = await frame.EvaluateAsync(
            "window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("child", main.Value.StringValue);

        // The page-level main world is the top document, not the iframe.
        EvaluationResult pageMain = await page.EvaluateAsync(
            "window.mainWorldValue",
            isolatedContext: false);
        Assert.Equal("main", pageMain.Value.StringValue);

        Locator inner = frame.Locator("#inner");
        Assert.Equal("child-dom", await inner.TextContentAsync());
        Assert.Equal(
            "undefined",
            (await inner.EvaluateAsync("el => typeof window.mainWorldValue")).Value.StringValue);
        Assert.Equal(
            "string",
            (await inner.EvaluateAsync(
                "el => typeof window.mainWorldValue",
                isolatedContext: false)).Value.StringValue);

        // A frame-owner element belongs to its parent document; resolving it must not be routed
        // to the child frame just because DOM.describeNode reports the child frame id on it.
        Assert.Equal(
            "IFRAME",
            (await page.Locator("#frame").EvaluateAsync("el => el.tagName")).Value.StringValue);
    }

    [Fact]
    public async Task Locator_chaining_and_within_scope_to_descendants()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        Assert.Equal(2, await page.Locator(".card").Locator("button.action").CountAsync());
        Assert.Equal(
            2,
            await page.Locator("button.action").Within(page.Locator(".card")).CountAsync());
        Assert.Equal(
            2,
            await page.Locator(".card").Locator(page.Locator("button.action")).CountAsync());
        Assert.Equal(
            "inside",
            await page.Locator(".card").Locator("button.action").First().TextContentAsync());
    }

    [Fact]
    public async Task Locator_wait_for_function_polls_until_truthy()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        ValueTask<JSHandle> waiting = page
            .Locator("#ready")
            .WaitForFunctionAsync(
                "el => el.dataset.ready === '1'",
                new LocatorWaitForFunctionOptions { Timeout = TimeSpan.FromSeconds(10) });

        // The condition is false on arrival, so the wait must still be polling.
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        await page.EvaluateAsync(
            "document.getElementById('ready').dataset.ready = '1'");

        await using JSHandle handle = await waiting;
        Assert.NotNull(await handle.JsonValueAsync());
    }

    [Fact]
    public async Task Page_eval_on_selector_and_aria_snapshot()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        EvaluationResult single = await page.EvalOnSelectorAsync(
            "#value",
            "el => el.textContent");
        Assert.Equal("dom", single.Value.StringValue);

        EvaluationResult all = await page.EvalOnSelectorAllAsync(
            ".item",
            "els => els.length");
        Assert.True(all.Value.TryGetInt64(out long count));
        Assert.Equal(3, count);

        await Assert.ThrowsAsync<CdpStrictModeException>(
            () => page.EvalOnSelectorAsync(
                ".item",
                "el => el.textContent",
                new PageEvalOnSelectorOptions { Strict = true }).AsTask());

        string snapshot = await page.AriaSnapshotAsync();
        Assert.Contains("heading \"Title\"", snapshot, StringComparison.Ordinal);
        Assert.Contains("button \"Ready\"", snapshot, StringComparison.Ordinal);

        string shallow = await page.AriaSnapshotAsync(new PageAriaSnapshotOptions { Depth = 0 });
        Assert.DoesNotContain("heading", shallow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mouse_double_click_dispatches_a_dblclick_event()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        BoundingBox box = Assert.IsType<BoundingBox>(
            await page.Locator("#ready").BoundingBoxAsync());
        await page.Mouse.DblClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));

        Assert.Equal("dbl", await WaitForTitleAsync(page, "dbl"));
    }

    [Fact]
    public async Task Requests_async_lists_observed_requests()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        // Enabling observation before the navigation captures every request.
        await page.RequestsAsync();
        await page.GotoAsync(_server!.Url("/isolated"));
        await page.EvaluateAsync("fetch('/api').then(response => response.text())");

        IReadOnlyList<PageRequest> requests = await page.RequestsAsync();
        Assert.Contains(
            requests,
            request => request.Url.EndsWith("/isolated", StringComparison.Ordinal));
        Assert.Contains(
            requests,
            request => request.Url.EndsWith("/child", StringComparison.Ordinal));
        Assert.Contains(
            requests,
            request => request.Url.EndsWith("/api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Route_fetch_can_fulfill_from_the_upstream_response()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        int routed = 0;
        await page.RouteAsync("**/api", async route =>
        {
            routed++;
            APIResponse upstream = await route.FetchAsync();
            await route.FulfillAsync(upstream);
        });

        ValueTask<PageResponse> waiting = page.WaitForResponseAsync("**/api");
        await page.GotoAsync(_server!.Url("/isolated"));
        EvaluationResult body = await page.EvaluateAsync(
            "fetch('/api').then(response => response.text())");
        PageResponse response = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, routed);
        Assert.Contains("upstream", body.Value.StringValue, StringComparison.Ordinal);
        Assert.Equal(200, response.Status);
        Assert.Equal("yes", await response.HeaderValueAsync("x-upstream"));
        Assert.Equal(
            "yes",
            (await response.AllHeadersAsync())["x-upstream"]);

        IReadOnlyList<CdpHeader> headers = await response.HeadersArrayAsync();
        Assert.Contains(
            headers,
            header => header.Name.Equals("X-Upstream", StringComparison.OrdinalIgnoreCase)
                && header.Value == "yes");

        System.Text.Json.JsonElement? json = await response.JsonAsync();
        Assert.NotNull(json);
        Assert.Equal("upstream", json.Value.GetProperty("from").GetString());
        Assert.Contains("upstream", await response.TextAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(await response.BodyAsync());
    }

    [Fact]
    public async Task NewBrowser_cdp_session_addresses_the_browser_endpoint()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        CdpSession session = await browser.Browser.NewBrowserCDPSessionAsync();
        RawJsonResult version = await session.SendAsync<RawJsonResult>("Browser.getVersion"u8);
        string json = Encoding.UTF8.GetString(version.Json.Span);
        Assert.Contains("\"product\"", json, StringComparison.Ordinal);

        // Browser-level sessions are the root connection; detaching is a no-op.
        await session.DetachAsync();
    }

    [Fact]
    public async Task Highlight_can_be_hidden_after_showing()
    {
        if (!TestBrowser.Require())
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/isolated"));

        await page.Locator("#value").HighlightAsync();
        await page.Locator("#value").HideHighlightAsync();
        await page.HideHighlightAsync();
    }

    private static async Task<string> WaitForTitleAsync(CdpPage page, string expected)
    {
        string title = string.Empty;
        for (int i = 0; i < 250; i++)
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
}
