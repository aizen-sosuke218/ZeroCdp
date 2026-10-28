using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>Playwright-compatible options for <see cref="CdpPage.GotoAsync(string, PageGotoOptions, CancellationToken)"/>.</summary>
public sealed class PageGotoOptions
{
    /// <summary>Lifecycle point to wait for. Defaults to <see cref="WaitUntilState.Load"/>.</summary>
    public WaitUntilState? WaitUntil { get; init; }

    /// <summary>Navigation timeout. Null uses <see cref="CdpPage.DefaultNavigationTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Playwright-compatible options for <see cref="CdpPage.ReloadAsync(PageReloadOptions, CancellationToken)"/>.</summary>
public sealed class PageReloadOptions
{
    public WaitUntilState? WaitUntil { get; init; }

    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.WaitForLoadStateAsync(LoadState, PageWaitForLoadStateOptions?, CancellationToken)"/>.</summary>
public sealed class PageWaitForLoadStateOptions
{
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.WaitForNavigationAsync(PageWaitForNavigationOptions?, CancellationToken)"/>.</summary>
public sealed class PageWaitForNavigationOptions
{
    /// <summary>URL glob the committed URL must match. Null matches any navigation.</summary>
    public string? Url { get; init; }

    /// <summary>Lifecycle point to wait for after the commit. Defaults to <see cref="WaitUntilState.Load"/>.</summary>
    public WaitUntilState? WaitUntil { get; init; }

    /// <summary>Navigation timeout. Null uses <see cref="CdpPage.DefaultNavigationTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.WaitForSelectorAsync(string, PageWaitForSelectorOptions, CancellationToken)"/>.</summary>
public sealed class PageWaitForSelectorOptions
{
    public WaitForSelectorState State { get; init; } = WaitForSelectorState.Visible;

    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.WaitForFunctionAsync(string, PageWaitForFunctionOptions, CancellationToken)"/>.</summary>
public sealed class PageWaitForFunctionOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Polling interval. Defaults to 50 ms, matching the native wait loop.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>Options for <see cref="CdpPage.AddScriptTagAsync(PageAddScriptTagOptions, CancellationToken)"/>.</summary>
public sealed class PageAddScriptTagOptions
{
    /// <summary>URL of an external script. Exactly one of Url, Path or Content is required.</summary>
    public string? Url { get; init; }

    /// <summary>Local script file to inline. Exactly one of Url, Path or Content is required.</summary>
    public string? Path { get; init; }

    /// <summary>Inline script body. Exactly one of Url, Path or Content is required.</summary>
    public string? Content { get; init; }

    /// <summary>Script type, for example <c>text/javascript</c> or <c>module</c>.</summary>
    public string? Type { get; init; }
}

/// <summary>Options for <see cref="CdpPage.AddStyleTagAsync(PageAddStyleTagOptions, CancellationToken)"/>.</summary>
public sealed class PageAddStyleTagOptions
{
    /// <summary>URL of an external stylesheet. Exactly one of Url, Path or Content is required.</summary>
    public string? Url { get; init; }

    /// <summary>Local CSS file to inline. Exactly one of Url, Path or Content is required.</summary>
    public string? Path { get; init; }

    /// <summary>Inline CSS body. Exactly one of Url, Path or Content is required.</summary>
    public string? Content { get; init; }
}

/// <summary>Paper format used by <see cref="CdpPage.PdfAsync(PdfOptions, CancellationToken)"/>.</summary>
public enum PdfFormat
{
    Letter = 0,
    Legal = 1,
    Tabloid = 2,
    Ledger = 3,
    A0 = 4,
    A1 = 5,
    A2 = 6,
    A3 = 7,
    A4 = 8,
    A5 = 9,
    A6 = 10,
}

/// <summary>Playwright-compatible options for <see cref="CdpPage.PdfAsync(PdfOptions, CancellationToken)"/>.</summary>
public sealed class PdfOptions
{
    public bool PrintBackground { get; init; }

    public bool Landscape { get; init; }

    public PdfFormat? Format { get; init; }

    /// <summary>Page width, for example <c>8.5in</c>. Overrides <see cref="Format"/>.</summary>
    public string? Width { get; init; }

    /// <summary>Page height, for example <c>11in</c>. Overrides <see cref="Format"/>.</summary>
    public string? Height { get; init; }

    public string? MarginTop { get; init; }

    public string? MarginRight { get; init; }

    public string? MarginBottom { get; init; }

    public string? MarginLeft { get; init; }

    public double Scale { get; init; } = 1;

    public string? PageRanges { get; init; }

    public bool PreferCSSPageSize { get; init; }

    public bool DisplayHeaderFooter { get; init; }

    public string? HeaderTemplate { get; init; }

    public string? FooterTemplate { get; init; }
}

/// <summary>Playwright-compatible options for page screenshots.</summary>
public sealed class PageScreenshotOptions
{
    /// <summary>When set, the image is also written to this path.</summary>
    public string? Path { get; init; }

    public bool FullPage { get; init; }

    public ScreenshotType Type { get; init; } = ScreenshotType.Png;

    /// <summary>JPEG quality from 1 to 100. Ignored for PNG.</summary>
    public int? Quality { get; init; }

    public bool OmitBackground { get; init; }

    /// <summary>Optional clip in CSS pixels. Ignored when <see cref="FullPage"/> is set.</summary>
    public ScreenshotClip? Clip { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Locators whose boxes are covered with <see cref="MaskColor"/> in the image.</summary>
    public IReadOnlyList<Locator>? Mask { get; init; }

    /// <summary>CSS colour used for masked elements. Defaults to <c>#FF00FF</c>.</summary>
    public string? MaskColor { get; init; }

    public ScreenshotAnimations Animations { get; init; } = ScreenshotAnimations.Allow;

    public ScreenshotCaret Caret { get; init; } = ScreenshotCaret.Hide;

    public ScreenshotScale Scale { get; init; } = ScreenshotScale.Css;

    /// <summary>Additional CSS applied while capturing, then removed.</summary>
    public string? Style { get; init; }
}
