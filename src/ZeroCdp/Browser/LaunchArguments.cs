namespace ZeroCdp.Browser;

/// <summary>Builds the Chromium command line for a given profile.</summary>
internal static class LaunchArguments
{
    /// <summary>
    /// Baseline flags that keep the browser usable and quiet. None of these is an automation
    /// marker; they are the flags a human-opened browser would plausibly end up with.
    /// </summary>
    private static readonly string[] Baseline =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-search-engine-choice-screen",
    ];

    /// <summary>
    /// The single flag that keeps <c>navigator.webdriver</c> false without patching any JS global.
    /// </summary>
    private const string AutomationControlledOff = "--disable-blink-features=AutomationControlled";

    /// <summary>
    /// Flags Playwright passes that are themselves detectable, so they are deliberately absent:
    /// <c>--enable-automation</c> (sets navigator.webdriver), <c>--disable-component-update</c>,
    /// <c>--disable-default-apps</c>, <c>--disable-extensions</c>,
    /// <c>--disable-popup-blocking</c>.
    /// </summary>
    public static List<string> Build(LaunchOptions options, string userDataDirectory, bool usePipe)
    {
        List<string> args = new(16);
        args.AddRange(Baseline);

        if (options.Stealth is StealthProfile.Default or StealthProfile.Minimal)
        {
            args.Add(AutomationControlledOff);
        }

        if (usePipe)
        {
            // No socket at all. POSIX maps CDP to child fds 3/4; on Windows the launcher appends
            // --remote-debugging-io-pipes with the two inherited anonymous-pipe handles.
            args.Add("--remote-debugging-pipe");
        }
        else
        {
            args.Add("--remote-debugging-port=0");
            // Chrome binds the debugger to loopback already; being explicit means a future default
            // change cannot silently expose the debugger to the network.
            args.Add("--remote-debugging-address=127.0.0.1");
        }

        args.Add($"--user-data-dir={userDataDirectory}");

        if (options.Headless)
        {
            args.Add("--headless=new");

            // Under the Default profile the GPU flag is left out: headless=new has a real
            // compositor, and `--disable-gpu` is an obvious automation marker in command lines.
            if (options.Stealth != StealthProfile.Default)
            {
                args.Add("--disable-gpu");
            }
        }

        if (options.DisableSandbox)
        {
            args.Add("--no-sandbox");
        }

        if (!string.IsNullOrEmpty(options.ProxyServer))
        {
            args.Add($"--proxy-server={options.ProxyServer}");

            if (!string.IsNullOrEmpty(options.ProxyBypassList))
            {
                args.Add($"--proxy-bypass-list={options.ProxyBypassList}");
            }

            // Without this, WebRTC can bypass the proxy and expose the real IP address.
            if (options.DisableNonProxiedUdp)
            {
                args.Add("--webrtc-ip-handling-policy=disable_non_proxied_udp");
            }
        }

        foreach (string extra in options.AdditionalArguments)
        {
            args.Add(extra);
        }

        args.Add("about:blank");
        return args;
    }
}
