using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public class BrowserContextTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-context-" + Guid.NewGuid().ToString("N"));
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
    public async Task Contexts_isolate_cookies_and_apply_emulation()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Path.Combine(_directory, "context.html");
        await File.WriteAllTextAsync(file, "<title>ctx</title><p>context</p>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        await using BrowserContext context = await browser.Browser.NewContextAsync(new BrowserContextOptions
        {
            Viewport = new ViewportSize(640, 480),
            UserAgent = "ZeroCDP-Context/1.0",
            Locale = "fr-FR",
            TimezoneId = "Europe/Paris",
            ColorScheme = PreferredColorScheme.Dark,
        });

        await context.SetCookiesAsync(
        [
            new CdpCookie
            {
                Name = "ctx-cookie",
                Value = "context-value",
                Url = "https://example.com/",
                Path = "/",
            },
        ]);

        CookiesResult contextCookies = await context.GetCookiesAsync();
        Assert.Contains(contextCookies.Cookies, c => c.Name == "ctx-cookie" && c.Value == "context-value");

        CdpPage page = await context.NewPageAsync(new Uri(file).AbsoluteUri);
        Assert.Same(context, page.Context);
        Assert.Equal(640, await EvalIntAsync(page, "innerWidth"));
        Assert.Equal(480, await EvalIntAsync(page, "innerHeight"));
        Assert.True(await EvalBoolAsync(page, "navigator.userAgent.includes('ZeroCDP-Context/1.0')"));
        Assert.Equal("fr-FR", await EvalStringAsync(page, "navigator.language"));
        Assert.Equal("Europe/Paris", await EvalStringAsync(page, "Intl.DateTimeFormat().resolvedOptions().timeZone"));
        Assert.True(await EvalBoolAsync(page, "matchMedia('(prefers-color-scheme: dark)').matches"));

        CookiesResult defaultCookies = await browser.Browser.DefaultContext.GetCookiesAsync();
        Assert.DoesNotContain(defaultCookies.Cookies, c => c.Name == "ctx-cookie");

        await context.DisposeAsync();
        Assert.True(context.IsDisposed);
        Assert.DoesNotContain(browser.Browser.Contexts, c => c.BrowserContextId == context.BrowserContextId);
    }

    [Fact]
    public async Task Default_context_still_creates_pages()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Path.Combine(_directory, "default.html");
        await File.WriteAllTextAsync(file, "<title>default</title>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);
        Assert.Same(browser.Browser.DefaultContext, page.Context);
        Assert.Equal("default", await page.TitleAsync());
        Assert.Contains(page, browser.Browser.Pages);
        Assert.Contains(page, browser.Browser.DefaultContext.Pages);
    }

    private static async ValueTask<int> EvalIntAsync(CdpPage page, string expression)
    {
        EvaluationResult result = await page.EvaluateAsync(expression);
        Assert.False(result.Threw);
        Assert.True(result.Value.TryGetInt64(out long value));
        return (int)value;
    }

    private static async ValueTask<bool> EvalBoolAsync(CdpPage page, string expression)
    {
        EvaluationResult result = await page.EvaluateAsync(expression);
        Assert.False(result.Threw);
        Assert.True(result.Value.TryGetBoolean(out bool value));
        return value;
    }

    private static async ValueTask<string> EvalStringAsync(CdpPage page, string expression)
    {
        EvaluationResult result = await page.EvaluateAsync(expression);
        Assert.False(result.Threw);
        return result.Value.StringValue ?? string.Empty;
    }
}
