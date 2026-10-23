namespace ZeroCdp.Browser;

/// <summary>Image format produced by <see cref="CdpPage.ScreenshotAsync(string, ScreenshotOptions?, CancellationToken)"/>.</summary>
public enum ScreenshotFormat
{
    Png = 0,
    Jpeg = 1,
}

/// <summary>How animations are treated while a screenshot is captured.</summary>
public enum ScreenshotAnimations
{
    /// <summary>Leave CSS animations and transitions running.</summary>
    Allow = 0,

    /// <summary>Freeze CSS animations and transitions at their current state.</summary>
    Disabled = 1,
}

/// <summary>How the text caret is treated while a screenshot is captured.</summary>
public enum ScreenshotCaret
{
    /// <summary>Hide the caret.</summary>
    Hide = 0,

    /// <summary>Leave the caret at its current position.</summary>
    Initial = 1,
}

/// <summary>Whether the CSS or device pixel ratio defines screenshot resolution.</summary>
public enum ScreenshotScale
{
    /// <summary>One image pixel per CSS pixel.</summary>
    Css = 0,

    /// <summary>Use the device scale factor.</summary>
    Device = 1,
}

/// <summary>Options for page screenshots.</summary>
public sealed class ScreenshotOptions
{
    /// <summary>Capture the whole scrollable page instead of the viewport.</summary>
    public bool FullPage { get; init; }

    public ScreenshotFormat Format { get; init; } = ScreenshotFormat.Png;

    /// <summary>JPEG quality from 1 to 100. Ignored for PNG.</summary>
    public int? Quality { get; init; }

    /// <summary>Render transparent backgrounds as transparent instead of white.</summary>
    public bool OmitBackground { get; init; }

    /// <summary>Optional clip in CSS pixels. Ignored when <see cref="FullPage"/> is set.</summary>
    public ZeroCdp.Protocol.ScreenshotClip? Clip { get; init; }

    /// <summary>Optional timeout for the capture. Currently advisory for page-level screenshots.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Locators whose boxes are covered with <see cref="MaskColor"/> in the image.</summary>
    public IReadOnlyList<Locator>? Mask { get; init; }

    /// <summary>CSS colour used for masked elements. Defaults to <c>#FF00FF</c>.</summary>
    public string? MaskColor { get; init; }

    /// <summary>Freeze CSS animations and transitions while capturing.</summary>
    public ScreenshotAnimations Animations { get; init; } = ScreenshotAnimations.Allow;

    /// <summary>Hide the text caret while capturing. The default matches Playwright.</summary>
    public ScreenshotCaret Caret { get; init; } = ScreenshotCaret.Hide;

    /// <summary>Resolution of the produced image.</summary>
    public ScreenshotScale Scale { get; init; } = ScreenshotScale.Css;

    /// <summary>Additional CSS applied while capturing, then removed.</summary>
    public string? Style { get; init; }
}
