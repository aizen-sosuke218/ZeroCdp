namespace ZeroCdp.Browser;

/// <summary>Playwright-compatible options for <see cref="Locator.ClickAsync(LocatorClickOptions, CancellationToken)"/>.</summary>
public sealed class LocatorClickOptions
{
    public MouseButton Button { get; init; } = MouseButton.Left;

    public int ClickCount { get; init; } = 1;

    /// <summary>Pause between mouse press and release.</summary>
    public TimeSpan? Delay { get; init; }

    public KeyModifiers Modifiers { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip actionability checks and dispatch the input directly.</summary>
    public bool Force { get; init; }

    /// <summary>Perform actionability checks but do not dispatch the input.</summary>
    public bool Trial { get; init; }

    /// <summary>Click at this offset from the element's top-left corner.</summary>
    public Position? Position { get; init; }
}

/// <summary>Options for <see cref="Locator.CheckAsync(LocatorCheckOptions, CancellationToken)"/>.</summary>
public sealed class LocatorCheckOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the visible/enabled checks and click the element directly.</summary>
    public bool Force { get; init; }

    /// <summary>Perform actionability checks but do not click the element.</summary>
    public bool Trial { get; init; }

    /// <summary>Click at this offset from the element's top-left corner.</summary>
    public Position? Position { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.FillAsync(string, LocatorFillOptions, CancellationToken)"/>.</summary>
public sealed class LocatorFillOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the visible/enabled checks while still requiring an editable target.</summary>
    public bool Force { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.PressAsync(string, LocatorPressOptions, CancellationToken)"/>.</summary>
public sealed class LocatorPressOptions
{
    public TimeSpan? Delay { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.TypeAsync(string, LocatorTypeOptions, CancellationToken)"/>.</summary>
public sealed class LocatorTypeOptions
{
    public TimeSpan? Delay { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.HoverAsync(LocatorHoverOptions, CancellationToken)"/>.</summary>
public sealed class LocatorHoverOptions
{
    public KeyModifiers Modifiers { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the visible check and move to the element's centre.</summary>
    public bool Force { get; init; }

    /// <summary>Perform actionability checks but do not move the mouse.</summary>
    public bool Trial { get; init; }

    /// <summary>Hover at this offset from the element's top-left corner.</summary>
    public Position? Position { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.WaitForAsync(LocatorWaitForOptions, CancellationToken)"/>.</summary>
public sealed class LocatorWaitForOptions
{
    public WaitForSelectorState State { get; init; } = WaitForSelectorState.Visible;

    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="Locator.ScreenshotAsync(LocatorScreenshotOptions, CancellationToken)"/>.</summary>
public sealed class LocatorScreenshotOptions
{
    public string? Path { get; init; }

    public ScreenshotType Type { get; init; } = ScreenshotType.Png;

    public int? Quality { get; init; }

    public bool OmitBackground { get; init; }

    public TimeSpan? Timeout { get; init; }

    public IReadOnlyList<Locator>? Mask { get; init; }

    public string? MaskColor { get; init; }

    public ScreenshotAnimations Animations { get; init; } = ScreenshotAnimations.Allow;

    public ScreenshotCaret Caret { get; init; } = ScreenshotCaret.Hide;

    public ScreenshotScale Scale { get; init; } = ScreenshotScale.Css;

    public string? Style { get; init; }
}

/// <summary>Options for <see cref="Locator.SelectOptionAsync(IReadOnlyList{string}, LocatorSelectOptionOptions, CancellationToken)"/>.</summary>
public sealed class LocatorSelectOptionOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the visible/enabled checks and select the first match directly.</summary>
    public bool Force { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }

    /// <summary>Option values to select. Mutually exclusive with Labels and Indexes.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Visible option labels to select. Mutually exclusive with Values and Indexes.</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>Zero-based option indexes to select. Mutually exclusive with Values and Labels.</summary>
    public IReadOnlyList<int>? Indexes { get; init; }
}

/// <summary>Options for <see cref="Locator.DragToAsync(Locator, LocatorDragToOptions, CancellationToken)"/>.</summary>
public sealed class LocatorDragToOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the visible/enabled checks and drag from the element's centre.</summary>
    public bool Force { get; init; }

    /// <summary>Perform actionability checks but do not dispatch the drag.</summary>
    public bool Trial { get; init; }

    /// <summary>Drag from this offset relative to the source element's top-left corner.</summary>
    public Position? SourcePosition { get; init; }

    /// <summary>Drop at this offset relative to the target element's top-left corner.</summary>
    public Position? TargetPosition { get; init; }

    /// <summary>Kept for Playwright compatibility; the trusted drag sequence does not hold modifier keys.</summary>
    public KeyModifiers Modifiers { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.SetInputFilesAsync(IReadOnlyList{string}, LocatorSetInputFilesOptions, CancellationToken)"/>.</summary>
public sealed class LocatorSetInputFilesOptions
{
    public TimeSpan? Timeout { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}

/// <summary>Options for <see cref="Locator.TapAsync(LocatorTapOptions, CancellationToken)"/>.</summary>
public sealed class LocatorTapOptions
{
    public KeyModifiers Modifiers { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip actionability checks and dispatch the touch sequence directly.</summary>
    public bool Force { get; init; }

    /// <summary>Perform actionability checks but do not dispatch the touch sequence.</summary>
    public bool Trial { get; init; }

    /// <summary>Tap at this offset from the element's top-left corner.</summary>
    public Position? Position { get; init; }

    /// <summary>Kept for Playwright compatibility; actions never wait for navigation by default.</summary>
    public bool NoWaitAfter { get; init; }
}
