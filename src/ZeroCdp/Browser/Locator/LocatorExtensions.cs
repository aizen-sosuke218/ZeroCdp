using ZeroCdp.Browser;

namespace ZeroCdp.Browser;

/// <summary>Descendant locator helpers for <see cref="Locator"/>.</summary>
public static class LocatorExtensions
{
    /// <summary>
    /// Creates a locator scoped to descendants of every element matched by <paramref name="parent"/>.
    /// This is the equivalent of Playwright's <c>locator.locator()</c> chaining.
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
}
