using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// Playwright-style per-page emulation options for <see cref="CdpBrowser.NewPageAsync(BrowserNewPageOptions, CancellationToken)"/>.
/// </summary>
public sealed class BrowserNewPageOptions
{
    public ViewportSize? Viewport { get; init; }

    public double DeviceScaleFactor { get; init; } = 1;

    public bool Mobile { get; init; }

    public string? UserAgent { get; init; }

    public string? Locale { get; init; }

    public string? TimezoneId { get; init; }

    public PreferredColorScheme? ColorScheme { get; init; }

    public Geolocation? Geolocation { get; init; }

    public bool JavaScriptEnabled { get; init; } = true;

    public bool BypassCsp { get; init; }

    /// <summary>Use strict locator mode for this page. Defaults to true.</summary>
    public bool StrictSelectors { get; init; } = true;

    public IReadOnlyList<CdpHeader> ExtraHttpHeaders { get; init; } = Array.Empty<CdpHeader>();

    internal BrowserContextOptions ToContextOptions()
        => new()
        {
            Viewport = Viewport,
            DeviceScaleFactor = DeviceScaleFactor,
            Mobile = Mobile,
            UserAgent = UserAgent,
            Locale = Locale,
            TimezoneId = TimezoneId,
            ColorScheme = ColorScheme,
            Geolocation = Geolocation,
            JavaScriptEnabled = JavaScriptEnabled,
            BypassCsp = BypassCsp,
            StrictSelectors = StrictSelectors,
            ExtraHttpHeaders = ExtraHttpHeaders,
        };
}
