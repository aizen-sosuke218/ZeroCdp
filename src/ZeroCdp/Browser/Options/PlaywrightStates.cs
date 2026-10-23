namespace ZeroCdp.Browser;

/// <summary>
/// Playwright-compatible navigation lifecycle states. The existing <see cref="WaitUntil"/> enum
/// remains the native form; these overloads exist so code written against Playwright reads the
/// same way in ZeroCDP.
/// </summary>
public enum WaitUntilState
{
    /// <summary>Return as soon as the browser accepts the navigation.</summary>
    Commit = 0,

    /// <summary>Wait for <c>DOMContentLoaded</c>.</summary>
    DOMContentLoaded = 1,

    /// <summary>Wait for <c>load</c>.</summary>
    Load = 2,

    /// <summary>Wait for <c>load</c> and then 500 ms without an in-flight request.</summary>
    NetworkIdle = 3,
}

/// <summary>Playwright-compatible load states accepted by <c>WaitForLoadStateAsync</c>.</summary>
public enum LoadState
{
    /// <summary>Wait for <c>load</c>.</summary>
    Load = 0,

    /// <summary>Wait for <c>DOMContentLoaded</c>.</summary>
    DOMContentLoaded = 1,

    /// <summary>Wait for the network-idle window after <c>load</c>.</summary>
    NetworkIdle = 2,
}

/// <summary>Playwright-compatible selector wait states.</summary>
public enum WaitForSelectorState
{
    /// <summary>An element matches the selector.</summary>
    Attached = 0,

    /// <summary>No element matches the selector.</summary>
    Detached = 1,

    /// <summary>The first match has a non-empty box.</summary>
    Visible = 2,

    /// <summary>The first match is missing or has no box.</summary>
    Hidden = 3,
}

/// <summary>Playwright-compatible screenshot image type.</summary>
public enum ScreenshotType
{
    Png = 0,
    Jpeg = 1,
}

internal static class PlaywrightStateConversion
{
    public static WaitUntil ToWaitUntil(this WaitUntilState state) => state switch
    {
        WaitUntilState.Commit => WaitUntil.Commit,
        WaitUntilState.DOMContentLoaded => WaitUntil.DomContentLoaded,
        WaitUntilState.NetworkIdle => WaitUntil.NetworkIdle,
        _ => WaitUntil.Load,
    };

    public static WaitUntil ToWaitUntil(this LoadState state) => state switch
    {
        LoadState.DOMContentLoaded => WaitUntil.DomContentLoaded,
        LoadState.NetworkIdle => WaitUntil.NetworkIdle,
        _ => WaitUntil.Load,
    };

    public static WaitForState ToWaitForState(this WaitForSelectorState state) => state switch
    {
        WaitForSelectorState.Attached => WaitForState.Attached,
        WaitForSelectorState.Detached => WaitForState.Detached,
        WaitForSelectorState.Hidden => WaitForState.Hidden,
        _ => WaitForState.Visible,
    };

    public static ScreenshotFormat ToScreenshotFormat(this ScreenshotType type)
        => type == ScreenshotType.Jpeg ? ScreenshotFormat.Jpeg : ScreenshotFormat.Png;
}
