using ZeroCdp.Protocol;
using ZeroCdp.Selectors;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

/// <summary>
/// A frame selected by an <c>iframe</c>/<c>frame</c> element, used to scope locators and
/// evaluation to that frame's document.
/// </summary>
/// <remarks>
/// Resolution happens on every operation, so a frame that is re-created or navigated is picked up
/// again. Evaluation runs in an isolated world created for the frame; the Runtime domain is never
/// enabled. Locator fallback and semantic queries use a pierced snapshot of the frame document, so
/// closed shadow roots are reachable inside frames as well.
/// </remarks>
public sealed class FrameLocator
{
    private readonly CdpPage _page;
    private readonly FrameScope? _parent;
    private readonly bool _pierceShadowDom;

    internal FrameLocator(CdpPage page, FrameScope? parent, string selector, bool pierceShadowDom)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        _page = page;
        _parent = parent;
        Selector = selector;
        _pierceShadowDom = pierceShadowDom;
    }

    /// <summary>CSS selector that identifies the frame owner element.</summary>
    public string Selector { get; }

    /// <summary>Creates a locator scoped to this frame's document.</summary>
    public Locator Locator(string selector, bool pierceShadowDom = false, TimeSpan? timeout = null)
        => new(_page, selector, pierceShadowDom, this, timeout);

    /// <summary>Finds elements by normalized text content inside this frame.</summary>
    public Locator GetByText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Text, text, exact), timeout, frame: this);
    }

    /// <summary>Finds elements by ARIA role inside this frame.</summary>
    public Locator GetByRole(
        string role,
        string? name = null,
        bool exact = false,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return new Locator(_page, new LocatorSpec(LocatorKind.Role, role, exact, name), timeout, frame: this);
    }

    /// <summary>Finds an element by ARIA role with Playwright-style filters inside this frame.</summary>
    public Locator GetByRole(string role, RoleOptions options, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(options);
        return new Locator(_page, new LocatorSpec(LocatorKind.Role, role, options: options), timeout, frame: this);
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

    /// <summary>Finds a labelable control by label text inside this frame.</summary>
    public Locator GetByLabel(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Label, text, exact), timeout, frame: this);
    }

    /// <summary>Finds an input or textarea by placeholder inside this frame.</summary>
    public Locator GetByPlaceholder(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Placeholder, text, exact), timeout, frame: this);
    }

    /// <summary>Finds an element by <c>data-testid</c> inside this frame.</summary>
    public Locator GetByTestId(string testId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(testId);
        return new Locator(_page, new LocatorSpec(LocatorKind.TestId, testId), timeout, frame: this);
    }

    /// <summary>Finds an image by <c>alt</c> text inside this frame.</summary>
    public Locator GetByAltText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.AltText, text, exact), timeout, frame: this);
    }

    /// <summary>Finds an element by its <c>title</c> attribute inside this frame.</summary>
    public Locator GetByTitle(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Title, text, exact), timeout, frame: this);
    }

    /// <summary>Targets a nested frame inside this one.</summary>
    public FrameLocator Frame(string selector)
        => new(_page, this, selector, pierceShadowDom: false);

    /// <summary>Evaluates JavaScript in this frame's isolated world.</summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string frameId = await ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        IsolatedWorldRuntime world = _page.GetFrameRuntime(frameId);
        return await world.EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<string> ResolveFrameIdAsync(CancellationToken cancellationToken)
    {
        long owner = await ResolveOwnerAsync(cancellationToken).ConfigureAwait(false);
        return await _page.GetFrameIdAsync(owner, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long> ResolveOwnerAsync(CancellationToken cancellationToken)
    {
        long[] nodes = _parent is null
            ? _pierceShadowDom
                ? await _page.ResolveByMatcherAsync(CssSelectorParser.ParseList(Selector), cancellationToken).ConfigureAwait(false)
                : await _page.ResolveByQueryAsync(Selector, cancellationToken).ConfigureAwait(false)
            : await _page.ResolveInFrameAsync(_parent.Value, Selector, cancellationToken).ConfigureAwait(false);

        if (nodes.Length == 0)
        {
            throw new ElementNotFoundException(Selector);
        }

        return nodes[0];
    }
}
