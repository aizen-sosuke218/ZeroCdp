using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// End-to-end checks for the M5 gate: the CDP trace must not contain any domain that makes the
/// page observable as automated, and evaluate must run in an isolated world.
/// </summary>
public class StealthTests : IAsyncLifetime
{
    private static readonly string[] ForbiddenCommands =
    [
        "Runtime.enable",
        "Console.enable",
        "Debugger.enable",
        "Target.setDiscoverTargets",
        "Page.addScriptToEvaluateOnNewDocument",
        "Page.addScriptToEvaluateOnLoad",
        "Runtime.addBinding",
        "Page.setBypassCSP",
    ];

    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-stealth-" + Guid.NewGuid().ToString("N"));
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
    public async Task Trace_never_enables_a_detectable_domain()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("trace.html", "<p id=\"p\">trace</p>");
        CommandRecorder recorder = new();

        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.EvaluateAsync("document.title");
        await page.EvaluateFunctionAsync("function() { return 1; }");

        string[] methods = recorder.Methods;

        // The isolated world is the only JavaScript entry point, and it is created explicitly.
        Assert.Contains("Page.createIsolatedWorld", methods);
        Assert.Contains("Runtime.evaluate", methods);
        Assert.Contains("Runtime.callFunctionOn", methods);

        foreach (string forbidden in ForbiddenCommands)
        {
            Assert.DoesNotContain(forbidden, methods);
        }
    }

    [Fact]
    public async Task Evaluate_runs_in_an_isolated_world_that_shares_the_dom()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write(
            "isolated.html",
            """
            <script>window.mainWorldMarker = 'main-world';</script>
            <p id="shared">shared-dom</p>
            """);

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        // A property defined by a page script is invisible in the isolated world...
        EvaluationResult marker = await page.EvaluateAsync("window.mainWorldMarker");
        Assert.True(marker.Value.IsUndefined);
        Assert.False(marker.Threw);

        // ...while the DOM is shared, so automation can still drive the page.
        EvaluationResult text = await page.EvaluateAsync("document.getElementById('shared').textContent");
        Assert.Equal("shared-dom", text.Value.StringValue);

        EvaluationResult changed = await page.EvaluateAsync("document.title = 'from-isolated'");
        Assert.False(changed.Threw);
        Assert.Equal("from-isolated", await page.TitleAsync());
    }

    [Fact]
    public async Task Main_world_does_not_receive_any_injected_marker()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        // This script asks the main world to look for the names an automation library would use.
        // Writing the answer into the title lets us read it without entering the main world.
        string file = Write(
            "injection.html",
            """
            <script>
              const markers = ['zerocdp', 'playwright', '__pw', 'cdp_'];
              const leaks = Object.getOwnPropertyNames(window)
                .filter(name => markers.some(marker => name.toLowerCase().includes(marker)));
              document.title = leaks.length === 0 ? 'clean' : 'leak:' + leaks.join(',');
            </script>
            """);

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);
        await page.EvaluateAsync("1 + 1");

        Assert.Equal("clean", await page.TitleAsync());
    }

    [Fact]
    public async Task World_names_are_unique_per_page_and_do_not_identify_the_library()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("names.html", "<p>names</p>");
        CommandRecorder recorder = new();

        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage first = await browser.Browser.NewPageAsync();
        CdpPage second = await browser.Browser.NewPageAsync();
        await first.GotoAsync(new Uri(file).AbsoluteUri);
        await second.GotoAsync(new Uri(file).AbsoluteUri);

        await first.EvaluateAsync("1");
        await second.EvaluateAsync("1");

        string[] names = recorder.WorldNames;
        Assert.Equal(2, names.Length);
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(first.IsolatedWorld.WorldName, names);
        Assert.Contains(second.IsolatedWorld.WorldName, names);

        foreach (string name in names)
        {
            Assert.DoesNotContain("zerocdp", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playwright", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cdp", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Isolated_world_is_recreated_after_a_navigation()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string first = Write("first.html", "<script>document.title = 'First';</script>");
        string second = Write("second.html", "<script>document.title = 'Second';</script>");

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.GotoAsync(new Uri(first).AbsoluteUri);
        EvaluationResult firstTitle = await page.EvaluateAsync("document.title");
        Assert.Equal("First", firstTitle.Value.StringValue);

        await page.GotoAsync(new Uri(second).AbsoluteUri);
        EvaluationResult secondTitle = await page.EvaluateAsync("document.title");
        Assert.Equal("Second", secondTitle.Value.StringValue);

        // Two documents, two create calls; a stale context must never surface to the caller.
        Assert.Equal(2, recorder.WorldNames.Length);
    }

    [Fact]
    public async Task Evaluates_values_functions_promises_and_exceptions()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        string file = Write("values.html", "<p>values</p>");
        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        EvaluationResult number = await page.EvaluateAsync("40 + 2");
        Assert.True(number.Value.TryGetInt64(out long sum));
        Assert.Equal(42, sum);

        EvaluationResult text = await page.EvaluateAsync("'hello \"world\"'");
        Assert.Equal("hello \"world\"", text.Value.StringValue);

        EvaluationResult boolean = await page.EvaluateAsync("navigator.webdriver");
        Assert.True(boolean.Value.TryGetBoolean(out bool webdriver));
        Assert.False(webdriver);

        EvaluationResult promise = await page.EvaluateAsync("Promise.resolve(7)");
        Assert.True(promise.Value.TryGetInt64(out long promised));
        Assert.Equal(7, promised);

        EvaluationResult function = await page.EvaluateFunctionAsync(
            "function(a, b) { return a + b; }",
            """[{"value":2},{"value":40}]""");
        Assert.True(function.Value.TryGetInt64(out long functionSum));
        Assert.Equal(42, functionSum);

        EvaluationResult failure = await page.EvaluateAsync("throw new Error('boom')");
        Assert.True(failure.Threw);
        Assert.Contains("boom", failure.ExceptionSummary, StringComparison.Ordinal);

        EvaluationResult referenceFailure = await page.EvaluateAsync("missing.property");
        Assert.True(referenceFailure.Threw);
        Assert.False(string.IsNullOrEmpty(referenceFailure.ExceptionSummary));
    }

    /// <summary>
    /// Network e2e is opt-in: it is a signal, not a merge gate. Run with
    /// <c>ZEROCDP_E2E_NETWORK=1</c> when the machine has internet access.
    /// </summary>
    [Fact]
    public async Task Passes_the_sannysoft_webdriver_row_when_network_tests_are_enabled()
    {
        if (Environment.GetEnvironmentVariable("ZEROCDP_E2E_NETWORK") != "1" || BrowserLocator.Find() is null)
        {
            return;
        }

        CommandRecorder recorder = new();
        await using LaunchedBrowser browser = await LaunchAsync(recorder);
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.GotoAsync("https://bot.sannysoft.com/", WaitUntil.Load, TimeSpan.FromSeconds(60));

        EvaluationResult webdriver = await page.EvaluateAsync(
            "(document.querySelector('#webdriver-result') || {}).textContent || ''");

        string text = webdriver.Value.StringValue ?? string.Empty;
        Assert.DoesNotContain("failed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("passed", text, StringComparison.OrdinalIgnoreCase);
    }

    private static ValueTask<LaunchedBrowser> LaunchAsync(CommandRecorder recorder)
        => BrowserLauncher.LaunchAsync(
            new LaunchOptions
            {
                Headless = true,
                StartupTimeout = TimeSpan.FromSeconds(30),
            },
            new CdpConnectionOptions { CommandObserver = recorder.Observe });

    private string Write(string name, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"<!doctype html><html><head><title>start</title></head><body>{body}</body></html>");
        return path;
    }

    /// <summary>Records every outbound command so a test can inspect the CDP trace.</summary>
    private sealed class CommandRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<byte[]> _frames = new();

        public void Observe(ReadOnlySpan<byte> frame)
        {
            lock (_gate)
            {
                _frames.Add(frame.ToArray());
            }
        }

        public string[] Methods
        {
            get
            {
                byte[][] frames = Snapshot();
                List<string> methods = new(frames.Length);
                foreach (byte[] frame in frames)
                {
                    string method = ReadMethod(frame);
                    if (method.Length != 0)
                    {
                        methods.Add(method);
                    }
                }

                return methods.ToArray();
            }
        }

        public string[] WorldNames
        {
            get
            {
                byte[][] frames = Snapshot();
                List<string> names = new(2);
                foreach (byte[] frame in frames)
                {
                    string? name = ReadWorldName(frame);
                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }

                return names.ToArray();
            }
        }

        private byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _frames.ToArray();
            }
        }

        private static string ReadMethod(ReadOnlySpan<byte> frame)
        {
            JsonCursor cursor = new(frame);
            if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("method"u8))
            {
                return string.Empty;
            }

            return cursor.TryReadString(out ReadOnlySpan<byte> raw, out _)
                ? Encoding.UTF8.GetString(raw)
                : string.Empty;
        }

        private static string? ReadWorldName(ReadOnlySpan<byte> frame)
        {
            if (!ReadMethod(frame).Equals("Page.createIsolatedWorld", StringComparison.Ordinal))
            {
                return null;
            }

            JsonCursor cursor = new(frame);
            if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("params"u8) || !cursor.TryEnterObject())
            {
                return null;
            }

            if (!cursor.TrySeekProperty("worldName"u8) || !cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                return null;
            }

            return Encoding.UTF8.GetString(raw);
        }
    }
}
