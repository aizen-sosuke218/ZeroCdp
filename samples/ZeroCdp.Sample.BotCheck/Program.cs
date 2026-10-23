using ZeroCdp;
using ZeroCdp.Browser;

// A manual, headful bot-detection session. ZeroCDP opens a real browser window and keeps the
// CDP session attached; you drive the browser yourself. The profile is persistent, so cookies
// and storage survive across runs unless you remove the directory or pass --user-data-dir.
//
// Run with:
//   dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release
//
// Close the browser window or press Ctrl+C to end the sample.

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Options.PrintHelp();
    return 2;
}

if (options.ShowHelp)
{
    Options.PrintHelp();
    return 0;
}

string profileDirectory = Path.GetFullPath(
    options.UserDataDirectory ?? Options.DefaultProfileDirectory());
Directory.CreateDirectory(profileDirectory);

using CancellationTokenSource shutdown = new();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(
        new LaunchOptions
        {
            Headless = options.Headless,
            Stealth = options.Stealth,
            UserDataDirectory = profileDirectory,
            KeepProfile = true,
            StartupTimeout = TimeSpan.FromSeconds(30),
        },
        cancellationToken: shutdown.Token);

    BrowserContext context = launched.Browser.DefaultContext;
    CdpPage page = launched.Browser.Pages.Count > 0
        ? launched.Browser.Pages[0]
        : await context.NewPageAsync(cancellationToken: shutdown.Token);
    page.SetDefaultTimeout(15_000);

    Console.WriteLine("ZeroCDP manual bot-check session");
    Console.WriteLine($"browser : {launched.Browser.Version}");
    Console.WriteLine($"transport: {launched.Connection.TransportDescription}");
    Console.WriteLine($"profile : {profileDirectory}");
    Console.WriteLine($"mode    : {(options.Headless ? "headless" : "headful")}, stealth={options.Stealth}");
    Console.WriteLine();

    if (options.StartUrl is null)
    {
        await page.SetContentAsync(StartPageHtml.Value, shutdown.Token);
        Console.WriteLine("Start page links are ready. Navigate and test manually in the browser window.");
    }
    else
    {
        await page.GotoAsync(options.StartUrl, WaitUntil.Load, TimeSpan.FromSeconds(60), shutdown.Token);
        Console.WriteLine($"Opened: {options.StartUrl}");
    }

    Console.WriteLine();
    Console.WriteLine("Suggested sites:");
    Console.WriteLine("  https://bot.sannysoft.com/");
    Console.WriteLine("  https://bot-detector.rebrowser.net/");
    Console.WriteLine("  https://abrahamjuliot.github.io/creepjs/");
    Console.WriteLine("  https://www.browserscan.net/bot-detection");
    Console.WriteLine("  https://pixelscan.net/");
    Console.WriteLine();
    Console.WriteLine("The session stays open until you close the browser window or press Ctrl+C.");
    Console.WriteLine();

    await WatchSessionAsync(launched, shutdown.Token);
    Console.WriteLine("Session ended.");
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.WriteLine("Interrupted; closing the browser.");
}
catch (CdpException ex)
{
    Console.Error.WriteLine("Could not start the browser: " + ex.Message);
    Console.Error.WriteLine(
        "On Linux/macOS, run this sample from a graphical session with DISPLAY or WAYLAND_DISPLAY set.");
    Environment.ExitCode = 1;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.Error.WriteLine("Bot-check sample failed: " + ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

return 0;

static async Task WatchSessionAsync(LaunchedBrowser launched, CancellationToken cancellationToken)
{
    Dictionary<string, string> lastUrls = new(StringComparer.Ordinal);
    int lastPageCount = -1;

    while (!cancellationToken.IsCancellationRequested && launched.Browser.IsConnected)
    {
        CdpPage[] pages = [.. launched.Browser.Pages];
        if (pages.Length != lastPageCount)
        {
            lastPageCount = pages.Length;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] pages: {pages.Length}");
        }

        foreach (CdpPage current in pages)
        {
            string url;
            try
            {
                url = await current.UrlAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
            {
                continue;
            }

            if (url.Length == 0
                || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!lastUrls.TryGetValue(current.TargetId, out string? previous)
                || !url.Equals(previous, StringComparison.Ordinal))
            {
                lastUrls[current.TargetId] = url;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {url}");
            }
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

internal sealed class Options
{
    public bool ShowHelp { get; private init; }

    public bool Headless { get; private init; }

    public StealthProfile Stealth { get; private init; } = StealthProfile.Default;

    public string? UserDataDirectory { get; private init; }

    public string? StartUrl { get; private init; }

    public static Options Parse(string[] args)
    {
        bool showHelp = false;
        bool headless = false;
        StealthProfile stealth = StealthProfile.Default;
        string? userDataDirectory = null;
        string? startUrl = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--headless":
                    // Default is headful. This flag exists for a quick smoke test only.
                    headless = true;
                    break;
                case "--start":
                    startUrl = NextValue(args, ref i, argument);
                    break;
                case "--user-data-dir":
                    userDataDirectory = NextValue(args, ref i, argument);
                    break;
                case "--stealth":
                    string value = NextValue(args, ref i, argument);
                    stealth = value.ToLowerInvariant() switch
                    {
                        "default" => StealthProfile.Default,
                        "minimal" => StealthProfile.Minimal,
                        "raw" => StealthProfile.Raw,
                        _ => throw new ArgumentException(
                            "--stealth must be one of: default, minimal, raw."),
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        return new Options
        {
            ShowHelp = showHelp,
            Headless = headless,
            Stealth = stealth,
            UserDataDirectory = userDataDirectory,
            StartUrl = startUrl,
        };
    }

    public static string DefaultProfileDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return Path.Combine(Path.GetTempPath(), "zerocdp-botcheck-profile");
        }

        return Path.Combine(home, ".zerocdp", "botcheck-profile");
    }

    public static void PrintHelp()
    {
        Console.WriteLine("ZeroCDP manual bot-check session");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --start <url>           Open this URL instead of the local link page.");
        Console.WriteLine("  --user-data-dir <path>  Persistent Chromium profile directory.");
        Console.WriteLine("  --stealth <profile>     default, minimal, or raw (default: default).");
        Console.WriteLine("  --headless              Smoke-test mode; the default is a visible window.");
        Console.WriteLine("  -h, --help              Show this help.");
        Console.WriteLine();
        Console.WriteLine("The browser remains open until you close it or press Ctrl+C.");
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value after {option}.");
        }

        string value = args[++index];
        if (value.Length == 0)
        {
            throw new ArgumentException($"Empty value after {option}.");
        }

        return value;
    }
}

internal static class StartPageHtml
{
    public const string Value = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ZeroCDP manual bot-check</title>
          <style>
            :root { color-scheme: dark; font-family: system-ui, sans-serif; }
            body { margin: 0; background: #111318; color: #e7e9ee; }
            main { max-width: 760px; margin: 0 auto; padding: 48px 24px 64px; }
            h1 { margin: 0 0 8px; font-size: 28px; }
            p { line-height: 1.6; color: #b9bec9; }
            .status { margin: 24px 0; padding: 16px 20px; border: 1px solid #2b303b;
                      border-radius: 10px; background: #181b22; }
            .status div { margin: 6px 0; overflow-wrap: anywhere; }
            code { color: #9fe870; }
            ul { padding-left: 0; list-style: none; }
            li { margin: 10px 0; }
            a { color: #7dc4ff; text-decoration: none; }
            a:hover { text-decoration: underline; }
            .hint { font-size: 13px; color: #8b919d; }
          </style>
        </head>
        <body>
          <main>
            <h1>ZeroCDP manual bot-check</h1>
            <p>
              This is a real headful browser. Click a link or type any URL in the address bar.
              The sample keeps the CDP session attached while you inspect each page.
            </p>
            <div class="status">
              <div>User agent: <code id="ua">reading...</code></div>
              <div>navigator.webdriver: <code id="webdriver">reading...</code></div>
              <div>window.chrome: <code id="chrome">reading...</code></div>
            </div>
            <p>Suggested detection pages:</p>
            <ul>
              <li><a href="https://bot.sannysoft.com/">bot.sannysoft.com</a> &mdash; classic browser-property table</li>
              <li><a href="https://bot-detector.rebrowser.net/">bot-detector.rebrowser.net</a> &mdash; modern CDP/runtime leak tests</li>
              <li><a href="https://abrahamjuliot.github.io/creepjs/">abrahamjuliot.github.io/creepjs</a> &mdash; headless and stealth ratings</li>
              <li><a href="https://www.browserscan.net/bot-detection">browserscan.net/bot-detection</a> &mdash; browser and automation checks</li>
              <li><a href="https://pixelscan.net/">pixelscan.net</a> &mdash; fingerprint and consistency checks</li>
            </ul>
            <p class="hint">
              Tip: use Ctrl+click or middle-click for a new tab. ZeroCDP logs navigations in the
              terminal while the session is open.
            </p>
          </main>
          <script>
            document.getElementById('ua').textContent = navigator.userAgent;
            document.getElementById('webdriver').textContent = String(navigator.webdriver);
            document.getElementById('chrome').textContent = String(Boolean(window.chrome));
          </script>
        </body>
        </html>
        """;
}
