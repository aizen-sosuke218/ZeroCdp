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
    /// <summary>Captures a PNG screenshot of the viewport.</summary>
    public async ValueTask<byte[]> ScreenshotBytesAsync(
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        options ??= new ScreenshotOptions();
        string format = ValidateScreenshotOptions(options);

        bool fullPage = options.FullPage;
        ScreenshotClip clip = default;
        bool hasClip = false;

        if (fullPage)
        {
            LayoutMetricsResult metrics = await Session
                .SendAsync<LayoutMetricsResult>("Page.getLayoutMetrics"u8, cancellationToken)
                .ConfigureAwait(false);

            double width = Math.Ceiling(metrics.Width);
            double height = Math.Ceiling(metrics.Height);
            ValidateScreenshotSize(width, height);

            clip = new ScreenshotClip(0, 0, width, height);
            hasClip = true;
        }
        else if (options.Clip is { } requestedClip)
        {
            clip = requestedClip;
            hasClip = true;
            ValidateScreenshotSize(clip.Width, clip.Height);
        }

        if (!hasClip && options.Scale == ScreenshotScale.Device)
        {
            LayoutMetricsResult metrics = await Session
                .SendAsync<LayoutMetricsResult>("Page.getLayoutMetrics"u8, cancellationToken)
                .ConfigureAwait(false);
            double width = metrics.ViewportWidth > 0 ? metrics.ViewportWidth : 1280;
            double height = metrics.ViewportHeight > 0 ? metrics.ViewportHeight : 720;
            clip = new ScreenshotClip(0, 0, width, height, DeviceScaleFactor);
            hasClip = true;
        }
        else if (hasClip && options.Scale == ScreenshotScale.Device)
        {
            clip = new ScreenshotClip(clip.X, clip.Y, clip.Width, clip.Height, DeviceScaleFactor);
        }

        await using ScreenshotGuard guard = await BeginScreenshotAsync(options, cancellationToken)
            .ConfigureAwait(false);

        ScreenshotResult screenshot = await Session
            .SendAsync<ScreenshotResult, CaptureScreenshotParams>(
                "Page.captureScreenshot"u8,
                new CaptureScreenshotParams(
                    format,
                    options.Quality ?? 0,
                    hasClip,
                    clip,
                    captureBeyondViewport: fullPage,
                    fromSurface: true,
                    omitBackground: options.OmitBackground),
                cancellationToken)
            .ConfigureAwait(false);

        if (screenshot.Data.IsEmpty)
        {
            throw new CdpProtocolException("Page.captureScreenshot returned no image data.");
        }

        return screenshot.Data.ToArray();
    }

    private double DeviceScaleFactor => Context.Options?.DeviceScaleFactor ?? 1;

    private async ValueTask<ScreenshotGuard> BeginScreenshotAsync(
        ScreenshotOptions options,
        CancellationToken cancellationToken)
    {
        List<MaskRectangle> masks = new();
        if (options.Mask is { Count: > 0 })
        {
            foreach (Locator locator in options.Mask)
            {
                if (locator is null)
                {
                    continue;
                }

                try
                {
                    long[] nodeIds = await locator.ResolveAsync(cancellationToken).ConfigureAwait(false);
                    foreach (long nodeId in nodeIds)
                    {
                        BoundingBox? box = await GetNodeBoundingBoxAsync(nodeId, cancellationToken)
                            .ConfigureAwait(false);
                        if (box is { } value && value.Width > 0 && value.Height > 0)
                        {
                            masks.Add(new MaskRectangle(value.X, value.Y, value.Width, value.Height));
                        }
                    }
                }
                catch (CdpException)
                {
                    // A mask locator that no longer resolves simply contributes no rectangle.
                }
            }
        }

        string css = BuildScreenshotCss(options);
        string rectJson = BuildMaskJson(masks, options.MaskColor ?? "#FF00FF");

        string expression = $$"""
            (() => {
              const style = document.createElement('style');
              style.setAttribute('data-zc-shot', '');
              style.textContent = {{JsText.Quote(css)}};
              (document.head || document.documentElement).appendChild(style);
              const rects = {{rectJson}};
              for (const rect of rects) {
                const cover = document.createElement('div');
                cover.setAttribute('data-zc-shot', '');
                cover.style.position = 'fixed';
                cover.style.left = rect.x + 'px';
                cover.style.top = rect.y + 'px';
                cover.style.width = rect.width + 'px';
                cover.style.height = rect.height + 'px';
                cover.style.background = rect.color;
                cover.style.zIndex = '2147483647';
                cover.style.pointerEvents = 'none';
                document.documentElement.appendChild(cover);
              }
              return true;
            })()
            """;

        await EnableAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await _isolated.EvaluateAsync(expression, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Preparing the screenshot failed.");
        }

        return new ScreenshotGuard(this);
    }

    private static string BuildScreenshotCss(ScreenshotOptions options)
    {
        StringBuilder css = new();
        if (options.Caret == ScreenshotCaret.Hide)
        {
            css.Append("*{caret-color:transparent !important;}");
        }

        if (options.Animations == ScreenshotAnimations.Disabled)
        {
            css.Append("*,*::before,*::after{animation:none !important;transition:none !important;}");
        }

        if (!string.IsNullOrEmpty(options.Style))
        {
            css.Append(options.Style);
        }

        return css.ToString();
    }

    private static string BuildMaskJson(List<MaskRectangle> masks, string color)
    {
        if (masks.Count == 0)
        {
            return "[]";
        }

        StringBuilder json = new();
        json.Append('[');
        for (int i = 0; i < masks.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            MaskRectangle mask = masks[i];
            json.Append("{\"x\":");
            json.Append(mask.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            json.Append(",\"y\":");
            json.Append(mask.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            json.Append(",\"width\":");
            json.Append(mask.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            json.Append(",\"height\":");
            json.Append(mask.Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            json.Append(",\"color\":");
            json.Append(JsText.Quote(color));
            json.Append('}');
        }

        json.Append(']');
        return json.ToString();
    }

    private readonly record struct MaskRectangle(double X, double Y, double Width, double Height);

    private sealed class ScreenshotGuard : IAsyncDisposable
    {
        private readonly CdpPage _page;
        private int _disposed;

        public ScreenshotGuard(CdpPage page) => _page = page;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _page._isolated.EvaluateAsync(
                        "document.querySelectorAll('[data-zc-shot]').forEach(element => element.remove()); true",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // Navigation or page close can race with cleanup; there is nothing left to remove.
            }
        }
    }

    internal async ValueTask<byte[]> ScreenshotNodeBytesAsync(
        long nodeId,
        ScreenshotOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        options ??= new ScreenshotOptions();
        string format = ValidateScreenshotOptions(options);

        FrameContext context = ContextForNode(nodeId);
        await ScrollToNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        ContentQuads quads = await context.Session
            .SendAsync<ContentQuads, GetContentQuadsParams>(
                "DOM.getContentQuads"u8,
                new GetContentQuadsParams(RealNodeId(nodeId)),
                cancellationToken)
            .ConfigureAwait(false);

        if (quads.Quads.Length == 0)
        {
            throw new CdpException("The element has no visible box, so it cannot be captured.");
        }

        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (double[] quad in quads.Quads)
        {
            for (int i = 0; i + 1 < quad.Length; i += 2)
            {
                minX = Math.Min(minX, quad[i]);
                maxX = Math.Max(maxX, quad[i]);
                minY = Math.Min(minY, quad[i + 1]);
                maxY = Math.Max(maxY, quad[i + 1]);
            }
        }

        double width = Math.Ceiling(maxX - minX);
        double height = Math.Ceiling(maxY - minY);
        ValidateScreenshotSize(width, height);

        double scale = options.Scale == ScreenshotScale.Device ? DeviceScaleFactor : 1;
        await using ScreenshotGuard guard = await BeginScreenshotAsync(options, cancellationToken)
            .ConfigureAwait(false);

        ScreenshotResult screenshot = await context.Session
            .SendAsync<ScreenshotResult, CaptureScreenshotParams>(
                "Page.captureScreenshot"u8,
                new CaptureScreenshotParams(
                    format,
                    options.Quality ?? 0,
                    hasClip: true,
                    new ScreenshotClip(minX, minY, width, height, scale),
                    captureBeyondViewport: false,
                    fromSurface: true,
                    omitBackground: options.OmitBackground),
                cancellationToken)
            .ConfigureAwait(false);

        if (screenshot.Data.IsEmpty)
        {
            throw new CdpProtocolException("Page.captureScreenshot returned no image data.");
        }

        return screenshot.Data.ToArray();
    }

    /// <summary>Captures a screenshot and writes it to <paramref name="path"/>.</summary>
    public async ValueTask ScreenshotAsync(
        string path,
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures a screenshot and writes it to <paramref name="destination"/>.</summary>
    public async ValueTask ScreenshotAsync(
        Stream destination,
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a locator for <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">CSS selector.</param>
    /// <param name="pierceShadowDom">
    /// Evaluate the selector across shadow boundaries, including closed roots. The browser resolves
    /// a plain selector itself; piercing needs a DOM snapshot and the managed matcher, so it is
    /// opt-in and slower.
    /// </param>

    private static string ValidateScreenshotOptions(ScreenshotOptions options)
    {
        if (options.Quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Screenshot quality must be between 1 and 100.");
        }

        if (options.Quality.HasValue && options.Format != ScreenshotFormat.Jpeg)
        {
            throw new ArgumentException("Screenshot quality only applies to JPEG.", nameof(options));
        }

        return options.Format switch
        {
            ScreenshotFormat.Png => "png",
            ScreenshotFormat.Jpeg => "jpeg",
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private static void ValidateScreenshotSize(double width, double height)    {
        const double MaxDimension = 16_384;
        const double MaxPixels = 64_000_000;

        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
        {
            throw new CdpResourceLimitException(
                $"A full-page screenshot of {width:0}x{height:0} CSS pixels exceeds the " +
                $"{MaxDimension:0}x{MaxDimension:0} / {MaxPixels:0} pixel limit.");
        }
    }

    /// <summary>Playwright-style page screenshot returning the image bytes.</summary>
    public async ValueTask<byte[]> ScreenshotAsync(
        PageScreenshotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(options.Path))
        {
            string path = options.Path;
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
        }

        return data;
    }

    /// <summary>Playwright-style page screenshot bytes overload.</summary>
    public ValueTask<byte[]> ScreenshotBytesAsync(
        PageScreenshotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ScreenshotBytesAsync(ToNative(options), cancellationToken);
    }

    private static ScreenshotOptions ToNative(PageScreenshotOptions options) => new()
    {
        FullPage = options.FullPage,
        Format = options.Type.ToScreenshotFormat(),
        Quality = options.Quality,
        OmitBackground = options.OmitBackground,
        Clip = options.Clip,
        Timeout = options.Timeout,
        Mask = options.Mask,
        MaskColor = options.MaskColor,
        Animations = options.Animations,
        Caret = options.Caret,
        Scale = options.Scale,
        Style = options.Style,
    };
}
