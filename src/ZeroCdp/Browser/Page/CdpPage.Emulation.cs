using System.Text;
using ZeroCdp.Dom;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Selectors;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>Overrides the layout viewport for this page.</summary>
    public async ValueTask SetViewportSizeAsync(
        ViewportSize viewport,
        double deviceScaleFactor = 1,
        bool mobile = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await Session
            .SendAsync<Unit, SetDeviceMetricsOverrideParams>(
                "Emulation.setDeviceMetricsOverride"u8,
                new SetDeviceMetricsOverrideParams(viewport.Width, viewport.Height, deviceScaleFactor, mobile),
                cancellationToken)
            .ConfigureAwait(false);
        _viewportSize = viewport;
    }

    /// <summary>Emulates the <c>prefers-color-scheme</c> media feature.</summary>
    public async ValueTask EmulateColorSchemeAsync(
        PreferredColorScheme scheme,
        CancellationToken cancellationToken = default)
    {
        string value = scheme switch
        {
            PreferredColorScheme.Light => "light",
            PreferredColorScheme.Dark => "dark",
            _ => "no-preference",
        };

        await Session
            .SendAsync<Unit, SetEmulatedMediaParams>(
                "Emulation.setEmulatedMedia"u8,
                new SetEmulatedMediaParams(
                    media: null,
                    [new EmulatedMediaFeature("prefers-color-scheme", value)]),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Emulates media type and media features such as reduced motion or forced colors.</summary>
    public async ValueTask EmulateMediaAsync(
        string? media = null,
        PreferredColorScheme? colorScheme = null,
        string? reducedMotion = null,
        string? forcedColors = null,
        CancellationToken cancellationToken = default)
    {
        List<EmulatedMediaFeature> features = new();
        if (colorScheme is { } scheme)
        {
            features.Add(new EmulatedMediaFeature(
                "prefers-color-scheme",
                scheme switch
                {
                    PreferredColorScheme.Light => "light",
                    PreferredColorScheme.Dark => "dark",
                    _ => "no-preference",
                }));
        }

        if (!string.IsNullOrEmpty(reducedMotion))
        {
            features.Add(new EmulatedMediaFeature("prefers-reduced-motion", reducedMotion));
        }

        if (!string.IsNullOrEmpty(forcedColors))
        {
            features.Add(new EmulatedMediaFeature("forced-colors", forcedColors));
        }

        await Session
            .SendAsync<Unit, SetEmulatedMediaParams>(
                "Emulation.setEmulatedMedia"u8,
                new SetEmulatedMediaParams(media, features.Count == 0 ? null : features),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a geolocation override installed with <see cref="SetGeolocationAsync"/>.</summary>
    public async ValueTask ClearGeolocationAsync(CancellationToken cancellationToken = default)
        => await Session
            .SendAsync<Unit>("Emulation.clearGeolocationOverride"u8, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Prints the page to PDF and returns the bytes.</summary>
    public async ValueTask<byte[]> PdfAsync(
        bool printBackground = false,
        bool landscape = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        PdfResult result = await Session
            .SendAsync<PdfResult, PrintToPdfParams>(
                "Page.printToPDF"u8,
                new PrintToPdfParams(printBackground, landscape),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Data.ToArray();
    }

    /// <summary>Prints the page to PDF with Playwright-compatible options.</summary>
    public async ValueTask<byte[]> PdfAsync(
        PdfOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        if (options.Scale is <= 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PDF scale must be greater than 0 and at most 2.");
        }

        if (options.Format is not null && (!string.IsNullOrEmpty(options.Width) || !string.IsNullOrEmpty(options.Height)))
        {
            throw new ArgumentException("PdfOptions.Format cannot be combined with Width or Height.");
        }

        PdfResult result = await Session
            .SendAsync<PdfResult, PrintToPdfParams>(
                "Page.printToPDF"u8,
                new PrintToPdfParams(
                    options.PrintBackground,
                    options.Landscape,
                    options.Format is { } format ? FormatName(format) : null,
                    options.Width,
                    options.Height,
                    options.MarginTop,
                    options.MarginRight,
                    options.MarginBottom,
                    options.MarginLeft,
                    options.Scale,
                    options.PageRanges,
                    options.PreferCSSPageSize,
                    options.DisplayHeaderFooter,
                    options.HeaderTemplate,
                    options.FooterTemplate),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Data.ToArray();
    }

    /// <summary>Prints the page to PDF with Playwright-compatible options and writes it to a path.</summary>
    public async ValueTask PdfAsync(
        string path,
        PdfOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await PdfAsync(options, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatName(PdfFormat format) => format switch
    {
        PdfFormat.Letter => "Letter",
        PdfFormat.Legal => "Legal",
        PdfFormat.Tabloid => "Tabloid",
        PdfFormat.Ledger => "Ledger",
        PdfFormat.A0 => "A0",
        PdfFormat.A1 => "A1",
        PdfFormat.A2 => "A2",
        PdfFormat.A3 => "A3",
        PdfFormat.A4 => "A4",
        PdfFormat.A5 => "A5",
        PdfFormat.A6 => "A6",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Prints the page to PDF at <paramref name="path"/>.</summary>
    public async ValueTask PdfAsync(
        string path,
        bool printBackground = false,
        bool landscape = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await PdfAsync(printBackground, landscape, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks V8 to collect garbage now.</summary>
    public async ValueTask RequestGCAsync(CancellationToken cancellationToken = default)
        => await Session
            .SendAsync<Unit>("HeapProfiler.collectGarbage"u8, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Freezes or resumes the page's JavaScript lifecycle.</summary>
    public async ValueTask SetWebLifecycleStateAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);
        if (!state.Equals("frozen", StringComparison.Ordinal)
            && !state.Equals("active", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "State must be 'frozen' or 'active'.");
        }

        await Session
            .SendAsync<Unit, SetWebLifecycleStateParams>(
                "Page.setWebLifecycleState"u8,
                new SetWebLifecycleStateParams(state),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Enables or disables JavaScript execution for subsequent documents.</summary>
    public async ValueTask SetJavaScriptEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => await Session
            .SendAsync<Unit, SetScriptExecutionDisabledParams>(
                "Emulation.setScriptExecutionDisabled"u8,
                new SetScriptExecutionDisabledParams(!enabled),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Brings the page's target to the foreground.</summary>
    public async ValueTask BringToFrontAsync(CancellationToken cancellationToken = default)
        => await Session.SendAsync<Unit>("Page.bringToFront"u8, cancellationToken).ConfigureAwait(false);

    /// <summary>Allows the page to execute inline/eval scripts even under a restrictive CSP.</summary>
    public async ValueTask SetBypassCspAsync(bool enabled = true, CancellationToken cancellationToken = default)
        => await Session
            .SendAsync<Unit, SetBypassCspParams>(
                "Page.setBypassCSP"u8,
                new SetBypassCspParams(enabled),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Overrides the geolocation reported to this page's JavaScript.</summary>
    public async ValueTask SetGeolocationAsync(
        Geolocation geolocation,
        CancellationToken cancellationToken = default)
        => await Session
            .SendAsync<Unit, SetGeolocationOverrideParams>(
                "Emulation.setGeolocationOverride"u8,
                new SetGeolocationOverrideParams(
                    geolocation.Latitude,
                    geolocation.Longitude,
                    geolocation.Accuracy),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Reject a click when the point would land on another element. On by default; it costs two
    /// extra protocol calls per click and never injects script into the page.
    /// </summary>
}
