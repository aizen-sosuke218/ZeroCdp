using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using BrowserSelectors = ZeroCdp.Browser.Selectors;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public class PlaywrightParityTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => new TestHttpResponse
            {
                Body = request.Path == "/json"
                    ? "{\"ok\":true,\"method\":\"" + request.Method + "\"}"
                    : "<!doctype html><title>parity</title><body></body>",
                ContentType = request.Path == "/json"
                    ? "application/json"
                    : "text/html; charset=utf-8",
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
    public async Task Assertions_handles_and_web_first_state_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();
        await page.SetContentAsync(
            """
            <!doctype html>
            <title>assertions</title>
            <div id="ready">hello</div>
            <input id="name" value="Alice">
            <input id="check" type="checkbox">
            <select id="sel">
              <option value="1">One</option>
              <option value="2">Two</option>
              <option value="3">Three</option>
            </select>
            <div id="parent"><span id="child">nested</span></div>
            """);

        LocatorAssertions ready = page.Locator("#ready").Expect();
        await ready.ToBeVisibleAsync(new ExpectOptions { Timeout = TimeSpan.FromSeconds(2) });
        await ready.ToHaveTextAsync("hello", new LocatorAssertionOptions { Timeout = TimeSpan.FromSeconds(2) });
        await page.Expect(page.Locator("#name")).ToHaveValueAsync("Alice");
        await page.Expect(page.Locator("#check")).Not.ToBeCheckedAsync();

        await page.Locator("#name").FillAsync("Bob");
        await page.Expect(page.Locator("#name")).ToHaveValueAsync("Bob");
        await page.Locator("#check").CheckAsync();
        await page.Expect(page.Locator("#check")).ToBeCheckedAsync();
        await page.Locator("#check").UncheckAsync();
        await page.Expect(page.Locator("#check")).Not.ToBeCheckedAsync();

        await page.Locator("#sel").SelectOptionAsync(new LocatorSelectOptionOptions
        {
            Labels = ["Two"],
        });
        Assert.Equal("2", await page.Locator("#sel").InputValueAsync());
        await page.Locator("#sel").SelectOptionAsync(new LocatorSelectOptionOptions
        {
            Indexes = [2],
        });
        Assert.Equal("3", await page.Locator("#sel").InputValueAsync());

        ElementHandle? input = await page.QuerySelectorAsync("#name");
        Assert.NotNull(input);
        await input.FillAsync("Carol");
        Assert.Equal("Carol", await input.InputValueAsync());
        ElementHandle? parent = await page.QuerySelectorAsync("#parent");
        Assert.NotNull(parent);
        ElementHandle? child = await parent.QuerySelectorAsync("#child");
        Assert.NotNull(child);
        Assert.Equal("nested", await child.TextContentAsync());
        await child.ClickAsync();
        await parent.DisposeAsync();

        JSHandle handle = await page.EvaluateHandleAsync("({ answer: 42, nested: { value: 'x' } })");
        JSHandle answer = await handle.GetPropertyAsync("answer");
        JsonElement? json = await answer.JsonValueAsync();
        Assert.NotNull(json);
        Assert.Equal(42, json.Value.GetInt32());
        await answer.DisposeAsync();
        await handle.DisposeAsync();

        await page.Locator("#name").Expect().ToBeInViewportAsync();
        await page.Locator("#name").FocusAsync();
        await page.Locator("#name").Expect().ToBeFocusedAsync();
    }

    [Fact]
    public async Task Content_tags_pdf_masks_and_base_url_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync(
            new BrowserContextOptions { BaseUrl = _server!.Url("/") });
        CdpPage page = await context.NewPageAsync();

        GotoResult result = await page.GotoAsync("page");
        Assert.Equal(_server.Url("/page"), result.Url);

        await page.SetContentAsync(
            """
            <!doctype html>
            <style>#styled { color: rgb(1, 2, 3); }</style>
            <div id="styled">style</div>
            <div id="secret">secret</div>
            """);

        Assert.Equal(1, await page.Locator("#styled").CountAsync());
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Content = "document.body.dataset.added = 'yes';",
        });
        Assert.Equal("yes", (await page.EvaluateAsync("document.body.dataset.added")).Value.StringValue);
        Assert.Equal(1, await page.Locator("#styled").CountAsync());

        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = "#styled { color: rgb(9, 8, 7); }",
        });
        Assert.Equal(
            2,
            await page.Locator("style").CountAsync());
        EvaluationResult styleResult = await page.EvaluateAsync(
            "getComputedStyle(document.querySelector('#styled')).color");
        Assert.False(
            styleResult.Threw,
            styleResult.ExceptionText ?? "style evaluation threw");
        Assert.True(
            styleResult.Value.HasValue,
            "style value missing; type=" + styleResult.Value.Type
                + " json=" + System.Text.Encoding.UTF8.GetString(styleResult.Value.ValueJson.Span));
        Assert.Equal("rgb(9, 8, 7)", styleResult.Value.StringValue);

        byte[] screenshot = await page.ScreenshotBytesAsync(new PageScreenshotOptions
        {
            Mask = [page.Locator("#secret")],
        });
        Assert.NotEmpty(screenshot);
        Assert.Equal(0, await page.Locator("[data-zc-shot]").CountAsync());

        string pdfPath = Path.Combine(Path.GetTempPath(), "zerocdp-parity-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await page.PdfAsync(pdfPath, new PdfOptions { Format = PdfFormat.A4, PrintBackground = true });
            Assert.True(File.Exists(pdfPath));
            Assert.True(new FileInfo(pdfPath).Length > 100);
        }
        finally
        {
            if (File.Exists(pdfPath))
            {
                File.Delete(pdfPath);
            }
        }
    }

    [Fact]
    public async Task Api_request_context_uses_base_url_and_reports_json()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using APIRequestContext api = await APIRequest.NewContextAsync(
            new APIRequestNewContextOptions
            {
                BaseUrl = _server!.Url("/"),
                ExtraHttpHeaders = new Dictionary<string, string>
                {
                    ["x-test"] = "value",
                },
            });

        await using APIResponse response = await api.PostAsync(
            "json",
            new APIRequestOptions
            {
                Data = "{\"x\":1}",
                Headers = new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                },
            });

        Assert.Equal(200, response.Status);
        Assert.True(response.Ok);
        JsonElement? json = await response.JsonAsync();
        Assert.NotNull(json);
        Assert.True(json.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("POST", json.Value.GetProperty("method").GetString());

        TestHttpRequest recorded = Assert.Single(_server.Requests);
        Assert.Equal("/json", recorded.Path);
        Assert.Equal("value", recorded.Headers["x-test"]);
        Assert.Equal("{\"x\":1}", recorded.Body);
    }

    [Fact]
    public async Task Context_api_request_shares_cookies_with_the_browser_context()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        _server!.Handler = request => request.Path switch
        {
            "/login" => new TestHttpResponse
            {
                Body = "logged in",
                Headers = new Dictionary<string, string>
                {
                    ["Set-Cookie"] = "session=from-api; Path=/; HttpOnly; SameSite=Lax",
                },
            },
            "/whoami" => new TestHttpResponse
            {
                ContentType = "text/plain; charset=utf-8",
                Body = request.Headers.TryGetValue("Cookie", out string? cookie)
                    ? cookie
                    : "(no cookie)",
            },
            _ => new TestHttpResponse { Body = "ok" },
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        Assert.Same(context.APIRequest, page.APIRequest);

        await using (APIResponse login = await context.APIRequest.GetAsync(_server.Url("/login")))
        {
            Assert.Equal(200, login.Status);
        }

        CookiesResult stored = await context.GetCookiesAsync();
        Assert.Contains(
            stored.Cookies,
            cookie => cookie.Name == "session" && cookie.Value == "from-api");

        await page.GotoAsync(_server.Url("/whoami"));
        Assert.Contains("session=from-api", await page.ContentAsync(), StringComparison.Ordinal);

        await context.SetCookiesAsync(
        [
            new CdpCookie
            {
                Name = "browser",
                Value = "yes",
                Url = _server.Url("/"),
            },
        ]);

        await using APIResponse whoami = await context.APIRequest.GetAsync(_server.Url("/whoami"));
        string body = await whoami.TextAsync();
        Assert.Contains("session=from-api", body, StringComparison.Ordinal);
        Assert.Contains("browser=yes", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_and_configurable_test_id_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();
        await page.SetContentAsync(
            """
            <!doctype html>
            <style>.covered { color: rgb(4, 5, 6); }</style>
            <div class="covered" data-hook="target">value</div>
            <script>
              function covered() { return 1; }
              covered();
            </script>
            """);

        string originalTestId = BrowserSelectors.TestIdAttribute;
        try
        {
            BrowserSelectors.SetTestIdAttribute("data-hook");
            Assert.Equal(1, await page.GetByTestId("target").CountAsync());
        }
        finally
        {
            BrowserSelectors.SetTestIdAttribute(originalTestId);
        }

        await page.Coverage.StartJSCoverageAsync();
        await page.EvaluateAsync("covered()");
        IReadOnlyList<JSCoverageEntry> scripts = await page.Coverage.StopJSCoverageAsync();
        Assert.NotEmpty(scripts);

        await page.Coverage.StartCSSCoverageAsync();
        await page.EvaluateAsync("document.querySelector('.covered').getBoundingClientRect()");
        IReadOnlyList<CssCoverageEntry> rules = await page.Coverage.StopCSSCoverageAsync();
        Assert.NotEmpty(rules);
    }

    [Fact]
    public async Task Expose_binding_receives_the_calling_page()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();
        await page.SetContentAsync("<div id='value'>x</div>");

        string? received = null;
        await page.ExposeBindingAsync("report", (source, value) =>
        {
            received = source.Page.MainFrame.Url + "|" + value;
            return "ok";
        });

        EvaluationResult result = await page.EvaluateAsync("window.report('payload')", isolatedContext: false);
        Assert.Equal("ok", result.Value.StringValue);
        for (int i = 0; i < 50 && received is null; i++)
        {
            await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(20));
        }

        Assert.NotNull(received);
        Assert.EndsWith("|payload", received);
    }
    [Fact]
    public async Task Context_http_credentials_answer_basic_auth_challenges()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        _server!.Handler = request =>
        {
            bool authorized = request.Headers.TryGetValue("Authorization", out string? header)
                && header == "Basic dXNlcjpwYXNz";
            return authorized
                ? new TestHttpResponse { Body = "<!doctype html><title>auth-ok</title><p>ok</p>" }
                : new TestHttpResponse
                {
                    Status = 401,
                    Body = "auth required",
                    Headers = new Dictionary<string, string>
                    {
                        ["WWW-Authenticate"] = "Basic realm=\"zerocdp\"",
                    },
                };
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync(
            new BrowserContextOptions
            {
                HttpCredentials = new HttpCredentials { Username = "user", Password = "pass" },
            });
        CdpPage page = await context.NewPageAsync();
        await page.GotoAsync(_server.Url("/protected"));
        Assert.Equal("auth-ok", await page.TitleAsync());
        Assert.Contains(
            _server.Requests,
            request => request.Headers.TryGetValue("Authorization", out string? value)
                && value == "Basic dXNlcjpwYXNz");
    }

}
