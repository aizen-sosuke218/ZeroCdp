using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

/// <summary>
/// One parsed locator selector: a Playwright-style engine prefix plus the compiled form of the
/// engine's query.
/// </summary>
/// <remarks>
/// Playwright accepts <c>css=</c>, <c>xpath=</c>, <c>text=</c>, <c>id=</c>, <c>data-testid=</c>
/// and <c>role=</c> prefixes, and treats a selector starting with <c>//</c> or <c>..</c> as XPath.
/// Parsing happens once per <see cref="Locator"/>, so a malformed selector still fails at the call
/// site instead of at the click.
/// </remarks>
internal sealed class SelectorPlan
{
    private SelectorPlan(string display)
    {
        Display = display;
    }

    /// <summary>Text shown in errors; the caller's selector, prefix included.</summary>
    public string Display { get; }

    /// <summary>CSS text for the browser fast path, when this is a CSS selector.</summary>
    public string? Css { get; private init; }

    /// <summary>Managed matcher for the pierced-snapshot fallback.</summary>
    public CompiledSelector[]? Compiled { get; private init; }

    /// <summary>Managed XPath evaluator over the pierced snapshot.</summary>
    public XPathSelector? XPath { get; private init; }

    /// <summary>Semantic query for the <c>text=</c>, <c>role=</c>, <c>id=</c> and test-id engines.</summary>
    public LocatorSpec? Spec { get; private init; }

    /// <summary>True when the caller asked for the pierced snapshot up front.</summary>
    public bool Pierce { get; private init; }

    /// <summary>
    /// True when the selector uses a pseudo-class Chrome does not implement
    /// (<c>:has-text()</c>, <c>:visible</c>), so the managed matcher must run directly instead of
    /// the <c>querySelectorAll</c> fast path.
    /// </summary>
    public bool ManagedOnly { get; private init; }

    public static SelectorPlan FromSpec(LocatorSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new SelectorPlan(spec.Display) { Spec = spec };
    }

    public static SelectorPlan Parse(string selector, bool pierceShadowDom)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);

        string text = selector.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("The selector is empty.", nameof(selector));
        }

        if (TryStripPrefix(text, "xpath=", out string xpathText) || LooksLikeXPath(text))
        {
            return new SelectorPlan(selector)
            {
                XPath = XPathSelector.Parse(xpathText.Length == 0 ? text : xpathText),
            };
        }

        if (TryStripPrefix(text, "css=", out string cssText))
        {
            bool managedOnly = RequiresManagedEngine(cssText);
            return new SelectorPlan(selector)
            {
                Css = cssText,
                Compiled = CssSelectorParser.ParseList(cssText),
                Pierce = pierceShadowDom,
                ManagedOnly = managedOnly,
            };
        }

        if (TryStripPrefix(text, "text=", out string textValue))
        {
            (string value, bool exact) = ParseTextValue(textValue);
            return new SelectorPlan(selector) { Spec = new LocatorSpec(LocatorKind.Text, value, exact) };
        }

        if (TryStripPrefix(text, "id=", out string id))
        {
            return new SelectorPlan(selector) { Spec = new LocatorSpec(LocatorKind.Id, id) };
        }

        if (TryStripPrefix(text, "data-testid=", out string testId))
        {
            return new SelectorPlan(selector) { Spec = new LocatorSpec(LocatorKind.TestId, testId) };
        }

        if (TryStripPrefix(text, "role=", out string roleQuery))
        {
            return new SelectorPlan(selector) { Spec = ParseRole(roleQuery) };
        }

        bool managed = RequiresManagedEngine(selector);
        return new SelectorPlan(selector)
        {
            Css = selector,
            Compiled = pierceShadowDom || managed ? CssSelectorParser.ParseList(selector) : null,
            Pierce = pierceShadowDom,
            ManagedOnly = managed,
        };
    }

    private static bool RequiresManagedEngine(string css)
        => css.Contains(":has-text(", StringComparison.OrdinalIgnoreCase)
            || css.Contains(":visible", StringComparison.OrdinalIgnoreCase);

    private static bool TryStripPrefix(string text, string prefix, out string value)
    {
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = text[prefix.Length..];
            return true;
        }

        value = text;
        return false;
    }

    private static bool LooksLikeXPath(string text)
        => text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith("../", StringComparison.Ordinal)
            || text.StartsWith("./", StringComparison.Ordinal)
            || text.StartsWith("/html", StringComparison.OrdinalIgnoreCase)
            || text == ".."
            || text == ".";

    private static (string Value, bool Exact) ParseTextValue(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return (trimmed[1..^1], true);
        }

        // Playwright also accepts a bare /regex/ form. The managed text engine compares strings,
        // so the delimiters are stripped and the pattern is matched literally.
        if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[^1] == '/')
        {
            return (trimmed[1..^1], false);
        }

        return (trimmed, false);
    }

    private static LocatorSpec ParseRole(string text)
    {
        string trimmed = text.Trim();
        int bracket = trimmed.IndexOf('[');
        if (bracket < 0)
        {
            return new LocatorSpec(LocatorKind.Role, trimmed);
        }

        string role = trimmed[..bracket].Trim();
        string filter = trimmed[(bracket + 1)..].TrimEnd(']').Trim();

        if (filter.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
        {
            string name = filter[5..].Trim();
            if (name.EndsWith("i", StringComparison.OrdinalIgnoreCase) && name.Length > 1 && name[^2] == '"')
            {
                name = name[..^1];
            }

            // Playwright's role engine treats the bracketed name as a case-insensitive,
            // whitespace-normalised substring; RoleOptions.Exact is the way to ask for equality.
            (string value, _) = ParseTextValue(name);
            return new LocatorSpec(LocatorKind.Role, role, exact: false, value);
        }

        return new LocatorSpec(LocatorKind.Role, role);
    }
}
