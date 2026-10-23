using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>A CSS-pixel viewport size.</summary>
public readonly struct ViewportSize
{
    public ViewportSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Playwright's conventional default viewport.</summary>
    public static ViewportSize Default => new(1280, 720);

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>Preferred colour scheme for emulated media queries.</summary>
public enum PreferredColorScheme
{
    Light = 0,
    Dark = 1,
    NoPreference = 2,
}

/// <summary>Emulated <c>prefers-reduced-motion</c> value.</summary>
public enum ReducedMotion
{
    NoPreference = 0,
    Reduce = 1,
}

/// <summary>Emulated <c>forced-colors</c> value.</summary>
public enum ForcedColors
{
    NoPreference = 0,
    Active = 1,
}

/// <summary>Emulated <c>prefers-contrast</c> value.</summary>
public enum PreferredContrast
{
    NoPreference = 0,
    More = 1,
    Less = 2,
    Custom = 3,
}

/// <summary>Whether service workers are allowed in a context.</summary>
public enum ServiceWorkerPolicy
{
    Allow = 0,
    Block = 1,
}

/// <summary>HTTP authentication credentials for a browser context.</summary>
public sealed class HttpCredentials
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>Only send credentials to this origin, for example <c>https://example.com</c>.</summary>
    public string? Origin { get; init; }

    /// <summary>Send credentials preemptively rather than after a challenge.</summary>
    public bool Send { get; init; }
}

/// <summary>A geographic location emulated for the page.</summary>
public readonly struct Geolocation
{
    public Geolocation(double latitude, double longitude, double accuracy = 1)
    {
        Latitude = latitude;
        Longitude = longitude;
        Accuracy = accuracy;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public double Accuracy { get; }
}

/// <summary>
/// Isolation and emulation settings for a browser context. Contexts created by
/// <see cref="CdpBrowser.NewContextAsync"/> get their own cookies, storage and cache.
/// </summary>
public sealed class BrowserContextOptions
{
    /// <summary>Viewport override applied to every page in the context. Null keeps the browser default.</summary>
    public ViewportSize? Viewport { get; init; }

    /// <summary>Device scale factor used with <see cref="Viewport"/>.</summary>
    public double DeviceScaleFactor { get; init; } = 1;

    public bool Mobile { get; init; }

    /// <summary>Base URL used to resolve relative navigations.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>User-Agent override. Null keeps the browser's native User-Agent.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Locale override, for example <c>en-US</c>.</summary>
    public string? Locale { get; init; }

    /// <summary>IANA timezone override, for example <c>America/Chicago</c>.</summary>
    public string? TimezoneId { get; init; }

    public PreferredColorScheme? ColorScheme { get; init; }

    public Geolocation? Geolocation { get; init; }

    /// <summary>Extra headers sent with every request from pages in this context.</summary>
    public IReadOnlyList<CdpHeader> ExtraHttpHeaders { get; init; } = Array.Empty<CdpHeader>();

    /// <summary>Run pages without JavaScript. The default keeps JavaScript enabled.</summary>
    public bool JavaScriptEnabled { get; init; } = true;

    /// <summary>Use strict locator mode for pages in this context. Defaults to true.</summary>
    public bool StrictSelectors { get; init; } = true;

    /// <summary>Allow pages to load inline/eval scripts even when CSP would reject them.</summary>
    public bool BypassCsp { get; init; }

    /// <summary>Ignore invalid TLS certificates for every page in this context.</summary>
    public bool IgnoreHttpsErrors { get; init; }

    /// <summary>Permissions granted to every page in the context.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>Allow downloads. Defaults to true.</summary>
    public bool AcceptDownloads { get; init; } = true;

    /// <summary>Enable touch event emulation.</summary>
    public bool HasTouch { get; init; }

    /// <summary>Emulate an offline network for every page in the context.</summary>
    public bool Offline { get; init; }

    /// <summary>Emulated screen size when it differs from the viewport.</summary>
    public ViewportSize? Screen { get; init; }

    public ReducedMotion? ReducedMotion { get; init; }

    public ForcedColors? ForcedColors { get; init; }

    public PreferredContrast? Contrast { get; init; }

    public ServiceWorkerPolicy ServiceWorkers { get; init; }

    /// <summary>HTTP authentication credentials used by pages in this context.</summary>
    public HttpCredentials? HttpCredentials { get; init; }

    /// <summary>
    /// Cookies and local storage to seed into a new incognito context. The default context is
    /// populated with <see cref="BrowserContext.SetStorageStateAsync"/> instead.
    /// </summary>
    public StorageState? StorageState { get; init; }

    /// <summary>
    /// Path of a HAR 1.2 file to write when the context closes. Null disables recording.
    /// Text response bodies are embedded; binary bodies are base64 encoded.
    /// </summary>
    public string? RecordHarPath { get; init; }

    /// <summary>Record a WebM video for every page in this context.</summary>
    public RecordVideoOptions? RecordVideo { get; init; }

    /// <summary>Proxy server for an incognito context, for example <c>http://127.0.0.1:8080</c>.</summary>
    public string? ProxyServer { get; init; }

    /// <summary>Semicolon-separated proxy bypass list.</summary>
    public string? ProxyBypassList { get; init; }

    internal void Validate()
    {
        if (DeviceScaleFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeviceScaleFactor));
        }

        if (Viewport is { } viewport && (viewport.Width <= 0 || viewport.Height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(Viewport));
        }

        if (Screen is { } screen && (screen.Width <= 0 || screen.Height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(Screen));
        }

        if (!string.IsNullOrEmpty(BaseUrl)
            && !Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("BaseUrl must be an absolute URL.", nameof(BaseUrl));
        }

        if (HttpCredentials is { } credentials
            && (string.IsNullOrEmpty(credentials.Username) || credentials.Password is null))
        {
            throw new ArgumentException("HttpCredentials requires a username and password.", nameof(HttpCredentials));
        }

        if (RecordVideo is { } video)
        {
            if (string.IsNullOrWhiteSpace(video.Dir))
            {
                throw new ArgumentException(
                    "RecordVideo.Dir must be a non-empty directory path.",
                    nameof(RecordVideo));
            }

            if (video.Size is { } size && (size.Width <= 0 || size.Height <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(RecordVideo));
            }

            if (!string.IsNullOrWhiteSpace(video.FfmpegPath)
                && !File.Exists(video.FfmpegPath))
            {
                throw new ArgumentException(
                    "RecordVideo.FfmpegPath does not exist.",
                    nameof(RecordVideo));
            }
        }

        ArgumentNullException.ThrowIfNull(ExtraHttpHeaders);
    }
}
