namespace ZeroCdp.Browser;

/// <summary>
/// Descendant locator helpers for <see cref="Locator"/>. C# does not allow an instance member
/// named <c>Locator</c> on the <see cref="Locator"/> type, so Playwright's chaining shape is
/// exposed as extensions.
/// </summary>
public static class LocatorExtensions
{
    /// <summary>
    /// Creates a locator scoped to descendants of every element matched by
    /// <paramref name="parent"/>. This is the equivalent of Playwright's
    /// <c>locator.locator(selector)</c> chaining.
    /// </summary>
    public static Locator Locator(
        this Locator parent,
        string selector,
        bool pierceShadowDom = false,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return parent.CreateChild(selector, pierceShadowDom, timeout);
    }

    /// <summary>
    /// Creates a locator matching the elements of <paramref name="selectorOrLocator"/> that are
    /// descendants of <paramref name="parent"/>'s matches.
    /// </summary>
    public static Locator Locator(
        this Locator parent,
        Locator selectorOrLocator,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(selectorOrLocator);
        if (ReferenceEquals(parent, selectorOrLocator))
        {
            return parent;
        }

        // Match every descendant of the parent, then intersect with the other locator's matches
        // by backend node id. Intersection keeps chained filters and nth() intact.
        Locator descendants = parent.CreateChild("*", pierceShadowDom: false, timeout);
        return descendants.And(selectorOrLocator);
    }
}
