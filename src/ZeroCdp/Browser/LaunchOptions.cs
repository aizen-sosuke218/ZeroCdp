using ZeroCdp.Security;

namespace ZeroCdp.Browser;

/// <summary>How much of the Patchright-style launch hardening to apply.</summary>
/// <remarks>
/// None of the profiles ever adds the flags Playwright passes that are themselves detectable:
/// <c>--enable-automation</c>, <c>--disable-component-update</c>, <c>--disable-default-apps</c>,
/// <c>--disable-extensions</c>, <c>--disable-popup-blocking</c>, <c>--remote-allow-origins=*</c>.
/// </remarks>
public enum StealthProfile
{
    /// <summary>
    /// No hardening beyond the neutral baseline. Use when you need exact Chromium behaviour;
    /// <c>navigator.webdriver</c> may be true.
    /// </summary>
    Raw = 0,

    /// <summary>
    /// Turns off <c>AutomationControlled</c> so <c>navigator.webdriver</c> is false, while keeping
    /// the conventional headless flags.
    /// </summary>
    Minimal = 1,

    /// <summary>
    /// Patchright's launch-flag posture: no automation flag and no <c>--disable-gpu</c>, which is
    /// a headless fingerprint a real browser would not have. This is the default.
    /// </summary>
    Default = 2,
}

/// <summary>How the CDP connection reaches the browser.</summary>
public enum CdpTransportKind
{
    /// <summary>
    /// DevTools pipe on every supported platform. POSIX uses child fds 3/4; Windows uses two
    /// inherited anonymous-pipe handles. No debugger port is opened.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// DevTools pipe explicitly. POSIX uses child fds 3/4; Windows uses
    /// <c>--remote-debugging-io-pipes</c> with inherited handles. No listening socket.
    /// </summary>
    Pipe = 1,

    /// <summary>Loopback WebSocket on a random port.</summary>
    Port = 2,
}

/// <summary>Options for starting a browser process.</summary>
public sealed class LaunchOptions
{
    /// <summary>
    /// Transport selection. <see cref="CdpTransportKind.Auto"/> and
    /// <see cref="CdpTransportKind.Pipe"/> keep the debugger off the network entirely on both POSIX
    /// and Windows. Use <see cref="CdpTransportKind.Port"/> only when a loopback WebSocket endpoint
    /// is required, for example to attach another client later.
    /// </summary>
    public CdpTransportKind Transport { get; init; } = CdpTransportKind.Auto;

    /// <summary>Explicit browser path. When null, <see cref="BrowserLocator"/> is used.</summary>
    public string? ExecutablePath { get; init; }

    public bool Headless { get; init; } = true;

    public StealthProfile Stealth { get; init; } = StealthProfile.Default;

    /// <summary>
    /// Directory used as <c>--user-data-dir</c>. A private temporary directory is created and
    /// removed on dispose when this is null.
    /// </summary>
    public string? UserDataDirectory { get; init; }

    /// <summary>Keep the private profile directory after the browser exits.</summary>
    public bool KeepProfile { get; init; }

    /// <summary>
    /// Pass <c>--no-sandbox</c>. Disabled by default: the Chromium sandbox is a real security
    /// boundary, and turning it off makes a malicious page far more dangerous.
    /// </summary>
    public bool DisableSandbox { get; init; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Let the browser inherit this process's stdout and stderr. Off by default so a library does
    /// not write into its host's console; turn it on when diagnosing a launch failure.
    /// </summary>
    public bool InheritBrowserOutput { get; init; }

    /// <summary>Extra command line arguments appended last.</summary>
    public IList<string> AdditionalArguments { get; init; } = new List<string>();

    /// <summary>
    /// Browser-wide proxy, for example <c>http://127.0.0.1:8080</c>. Per-context proxies are
    /// configured through <see cref="BrowserContextOptions.ProxyServer"/> instead.
    /// </summary>
    public string? ProxyServer { get; init; }

    /// <summary>Semicolon-separated proxy bypass list used with <see cref="ProxyServer"/>.</summary>
    public string? ProxyBypassList { get; init; }

    /// <summary>
    /// When a proxy is configured, keep WebRTC from using a non-proxied UDP path, which would leak
    /// the real IP address. On by default; it is Patchright's recommendation for proxied profiles.
    /// </summary>
    public bool DisableNonProxiedUdp { get; init; } = true;

    /// <summary>
    /// Allow-list enforced before top-level navigation, or <see cref="NavigationPolicy.AllowAll"/>
    /// by default. Set this when the page being automated is untrusted and must not be able to
    /// steer the browser to an arbitrary origin.
    /// </summary>
    public NavigationPolicy NavigationPolicy { get; init; } = NavigationPolicy.AllowAll;

    internal void Validate()
    {
        if (StartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        }

        if (ExecutablePath is not null && !File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("Browser executable not found.", ExecutablePath);
        }

        ArgumentNullException.ThrowIfNull(NavigationPolicy);
    }
}
