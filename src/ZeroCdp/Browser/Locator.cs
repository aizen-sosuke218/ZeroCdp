using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

/// <summary>Thrown when a locator matches nothing and an action needs an element.</summary>
public sealed class ElementNotFoundException : CdpException
{
    public ElementNotFoundException(string selector)
        : base($"No element matched the selector '{selector}'.")
    {
        Selector = selector;
    }

    public string Selector { get; }
}

/// <summary>Thrown when a locator action resolves to more than one element in strict mode.</summary>
public sealed class CdpStrictModeException : CdpException
{
    public CdpStrictModeException(string selector, int matches)
        : base($"Strict mode violation: locator '{selector}' resolved to {matches} elements.")
    {
        Selector = selector;
        Matches = matches;
    }

    public string Selector { get; }

    public int Matches { get; }
}

/// <summary>The state a locator can wait for.</summary>
public enum WaitForState
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

/// <summary>Mouse buttons supported by trusted click dispatch.</summary>
public enum MouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>Keyboard modifiers bit mask used by mouse and keyboard events (CDP layout).</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Meta = 4,
    Shift = 8,
}

/// <summary>A click offset relative to the element's top-left corner.</summary>
public readonly struct Position
{
    public Position(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }

    public double Y { get; }
}

/// <summary>Options for <see cref="Locator.ClickAsync(ClickOptions?, CancellationToken)"/>.</summary>
public sealed class ClickOptions
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

/// <summary>Filters applied by <see cref="Locator.Filter(LocatorFilterOptions)"/>.</summary>
public sealed class LocatorFilterOptions
{
    /// <summary>Keep elements whose text content contains this value.</summary>
    public string? HasText { get; init; }

    /// <summary>Drop elements whose text content contains this value.</summary>
    public string? HasNotText { get; init; }

    /// <summary>Keep elements that have a descendant matching this locator.</summary>
    public Locator? Has { get; init; }

    /// <summary>Drop elements that have a descendant matching this locator.</summary>
    public Locator? HasNot { get; init; }

    /// <summary>Keep only elements with a non-empty box.</summary>
    public bool Visible { get; init; }
}

/// <summary>Options for <see cref="Locator.AriaSnapshotAsync(LocatorAriaSnapshotOptions?, CancellationToken)"/>.</summary>
public sealed class LocatorAriaSnapshotOptions
{
    /// <summary>How long to wait for the element to be attached.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Drop ignored and presentational nodes, promoting their children. Defaults to true.</summary>
    public bool InterestingOnly { get; init; } = true;
}

/// <summary>Playwright-style options for <see cref="Locator.GetByRole(string, RoleOptions, TimeSpan?)"/>.</summary>
public sealed class RoleOptions
{
    /// <summary>Accessible name to match. Null skips the name check.</summary>
    public string? Name { get; init; }

    /// <summary>Require an exact, case-sensitive accessible-name match.</summary>
    public bool Exact { get; init; }

    /// <summary>Heading level (1–6) or <c>aria-level</c>.</summary>
    public int? Level { get; init; }

    public bool? Checked { get; init; }

    public bool? Disabled { get; init; }

    public bool? Expanded { get; init; }

    public bool? Pressed { get; init; }

    public bool? Selected { get; init; }

    /// <summary>Include elements hidden through <c>hidden</c> or <c>aria-hidden</c>.</summary>
    public bool? IncludeHidden { get; init; }
}

/// <summary>The visible box of an element in CSS pixels, relative to the main frame viewport.</summary>
public readonly struct BoundingBox
{
    public BoundingBox(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>
/// A selector resolved lazily against the live document.
/// </summary>
/// <remarks>
/// The locator holds no element handle: every action re-resolves, so a locator keeps working
/// after the page navigates or re-renders. Actions poll until the element is attached, visible and
/// enabled (and editable for text entry), then act. Polling stops at <see cref="Timeout"/> or the
/// page default, whichever is set.
/// </remarks>
public sealed partial class Locator
{

    private const int PollIntervalMs = 50;

    private readonly CdpPage _page;

    private readonly SelectorPlan _plan;

    private readonly FrameScope? _frame;

    private readonly Locator? _parent;

    private readonly int? _index;

    private readonly Locator? _filterSource;

    private readonly LocatorFilterOptions? _filter;

    private readonly Locator? _andSource;

    private readonly Locator? _andWith;

    private readonly Locator? _orSource;

    private readonly Locator? _orWith;

    private readonly Lock _fallbackGate = new();

    private CompiledSelector[]? _fallbackSelectors;

    private int _fallbackParseState;

    private long _nextFallbackTick;

    internal Locator(
        CdpPage page,
        string selector,
        bool pierceShadowDom,
        FrameScope? frame = null,
        TimeSpan? timeout = null,
        int? index = null,
        Locator? parent = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        ValidateTimeout(timeout);

        _page = page;
        _frame = frame;
        _parent = parent;
        _index = index;
        Timeout = timeout;

        // Parse eagerly so a bad selector fails at the call site, not at the click.
        _plan = SelectorPlan.Parse(selector, pierceShadowDom);
    }

    internal Locator(
        CdpPage page,
        LocatorSpec spec,
        TimeSpan? timeout = null,
        int? index = null,
        FrameScope? frame = null,
        Locator? parent = null)
        : this(page, SelectorPlan.FromSpec(spec), timeout, index, frame, parent)
    {
    }

    private Locator(
        CdpPage page,
        SelectorPlan plan,
        TimeSpan? timeout,
        int? index,
        FrameScope? frame,
        Locator? parent,
        Locator? filterSource = null,
        LocatorFilterOptions? filter = null,
        Locator? andSource = null,
        Locator? andWith = null,
        Locator? orSource = null,
        Locator? orWith = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateTimeout(timeout);

        _page = page;
        _plan = plan;
        _frame = frame;
        _parent = parent;
        _index = index;
        _filterSource = filterSource;
        _filter = filter;
        _andSource = andSource;
        _andWith = andWith;
        _orSource = orSource;
        _orWith = orWith;
        Timeout = timeout;
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public string Selector => _plan.Display;

    /// <summary>When true the selector is evaluated across shadow boundaries, including closed roots.</summary>
    public bool PierceShadowDom => _plan.Pierce;

    /// <summary>Per-locator timeout override; null uses <see cref="CdpPage.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>Creates a locator restricted to the first match.</summary>
    public Locator First() => WithIndex(0);

    /// <summary>Creates a locator restricted to the last match.</summary>
    public Locator Last() => WithIndex(-1);

    /// <summary>Creates a locator restricted to a zero-based match index.</summary>
    public Locator Nth(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return WithIndex(index);
    }

    /// <summary>
    /// Finds elements by normalized text content inside this locator's matches. Like
    /// <see cref="CdpPage.GetByText"/>, this reaches closed shadow roots.
    /// </summary>
    public Locator GetByText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return CreateChild(new LocatorSpec(LocatorKind.Text, text, exact), timeout);
    }

    /// <summary>Finds descendants by ARIA role and optional accessible name.</summary>
    public Locator GetByRole(
        string role,
        string? name = null,
        bool exact = false,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return CreateChild(new LocatorSpec(LocatorKind.Role, role, exact, name), timeout);
    }

    /// <summary>Finds descendants by ARIA role with Playwright-style filters.</summary>
    public Locator GetByRole(string role, RoleOptions options, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(options);
        return CreateChild(new LocatorSpec(LocatorKind.Role, role, options: options), timeout);
    }

    /// <summary>Finds a labelable descendant control by its associated label text.</summary>
    public Locator GetByLabel(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return CreateChild(new LocatorSpec(LocatorKind.Label, text, exact), timeout);
    }

    /// <summary>Finds a descendant input or textarea by its <c>placeholder</c>.</summary>
    public Locator GetByPlaceholder(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return CreateChild(new LocatorSpec(LocatorKind.Placeholder, text, exact), timeout);
    }

    /// <summary>Finds a descendant element by its <c>data-testid</c>.</summary>
    public Locator GetByTestId(string testId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(testId);
        return CreateChild(new LocatorSpec(LocatorKind.TestId, testId), timeout);
    }

    /// <summary>Finds a descendant image, area or image input by its <c>alt</c> text.</summary>
    public Locator GetByAltText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return CreateChild(new LocatorSpec(LocatorKind.AltText, text, exact), timeout);
    }

    /// <summary>Finds a descendant element by its <c>title</c> attribute.</summary>
    public Locator GetByTitle(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return CreateChild(new LocatorSpec(LocatorKind.Title, text, exact), timeout);
    }

    /// <summary>Playwright-style role lookup using the typed <see cref="AriaRole"/> enum.</summary>
    public Locator GetByRole(
        AriaRole role,
        RoleOptions? options = null,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), options ?? new RoleOptions(), timeout);

    /// <summary>Playwright-style role lookup with an accessible name.</summary>
    public Locator GetByRole(
        AriaRole role,
        string name,
        bool exact = false,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), name, exact, timeout);

    internal Locator CreateChild(string selector, bool pierceShadowDom, TimeSpan? timeout)
        => new(_page, selector, pierceShadowDom, _frame, timeout ?? Timeout, parent: this);

    internal Locator CreateChild(LocatorSpec spec, TimeSpan? timeout)
        => new(_page, spec, timeout ?? Timeout, frame: _frame, parent: this);

    /// <summary>Text content of every match, or of the selected nth match.</summary>

    private Locator WithIndex(int? index)
        => new(
            _page,
            _plan,
            Timeout,
            index,
            _frame,
            _parent,
            _filterSource,
            _filter,
            _andSource,
            _andWith,
            _orSource,
            _orWith);

    private long[] ApplyIndex(long[] nodes)
    {
        if (_index is null || nodes.Length == 0)
        {
            return _index is null ? nodes : [];
        }

        int index = _index.Value < 0 ? nodes.Length + _index.Value : _index.Value;
        return index >= 0 && index < nodes.Length ? [nodes[index]] : [];
    }
}
