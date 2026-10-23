using System.Text;
using ZeroCdp.Dom;

namespace ZeroCdp.Browser;

internal enum LocatorKind
{
    Text = 0,
    Role = 1,
    Label = 2,
    Placeholder = 3,
    TestId = 4,
    AltText = 5,
    Title = 6,
    Id = 7,
}

/// <summary>A selector expressed as a semantic query rather than CSS.</summary>
internal sealed class LocatorSpec
{
    public LocatorSpec(LocatorKind kind, string value, bool exact = false, string? name = null, RoleOptions? options = null)
    {
        Kind = kind;
        Value = kind is LocatorKind.Text
            or LocatorKind.Label
            or LocatorKind.Placeholder
            or LocatorKind.AltText
            or LocatorKind.Title
            ? TextNormalizer.Normalize(value)
            : value;
        Options = options;
        Exact = options?.Exact ?? exact;
        Name = options?.Name is { } optionName
            ? TextNormalizer.Normalize(optionName)
            : name is null ? null : TextNormalizer.Normalize(name);
    }

    public LocatorKind Kind { get; }

    public string Value { get; }

    public bool Exact { get; }

    /// <summary>Accessible name filter for <see cref="LocatorKind.Role"/>.</summary>
    public string? Name { get; }

    /// <summary>Playwright-style role filters (level, checked, expanded, …).</summary>
    public RoleOptions? Options { get; }

    public string Display => Kind switch
    {
        LocatorKind.Text => $"text=\"{Value}\"",
        LocatorKind.Role => Name is null ? $"role=\"{Value}\"" : $"role=\"{Value}\"[name=\"{Name}\"]",
        LocatorKind.Label => $"label=\"{Value}\"",
        LocatorKind.Placeholder => $"placeholder=\"{Value}\"",
        LocatorKind.TestId => $"{Selectors.TestIdAttribute}=\"{Value}\"",
        LocatorKind.AltText => $"alt=\"{Value}\"",
        LocatorKind.Title => $"title=\"{Value}\"",
        LocatorKind.Id => $"id=\"{Value}\"",
        _ => Value,
    };
}

/// <summary>Supporting data shared by every node check for one semantic query.</summary>
internal sealed class SemanticContext
{
    public SemanticContext(DomNode root)
    {
        Root = root;
        ById = new Dictionary<string, DomNode>(StringComparer.Ordinal);
        LabelTextById = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(root);
    }

    public DomNode Root { get; }

    public Dictionary<string, DomNode> ById { get; }

    public Dictionary<string, string> LabelTextById { get; }

    private void Collect(DomNode node)
    {
        if (node.IsElement)
        {
            string? id = node.GetAttribute("id");
            if (!string.IsNullOrEmpty(id))
            {
                ById[id] = node;
            }

            if (node.LocalName == "label")
            {
                string? target = node.GetAttribute("for");
                if (!string.IsNullOrEmpty(target))
                {
                    LabelTextById[target] = TextNormalizer.Normalize(node.TextContent());
                }
            }
        }

        foreach (DomNode child in node.TraversalChildren())
        {
            Collect(child);
        }
    }
}

internal static class LocatorSpecMatcher
{
    private static readonly HashSet<string> NoTextContentRoles = new(StringComparer.Ordinal)
    {
        "textbox", "searchbox", "slider", "spinbutton", "progressbar", "separator",
    };

    public static bool Matches(LocatorSpec spec, DomNode node, SemanticContext semantic)
    {
        if (!node.IsElement || node.NodeId == 0)
        {
            return false;
        }

        return spec.Kind switch
        {
            LocatorKind.Text => MatchesText(spec, node),
            LocatorKind.Role => MatchesRole(spec, node, semantic),
            LocatorKind.Label => MatchesLabel(spec, node, semantic),
            LocatorKind.Placeholder => MatchesPlaceholder(spec, node),
            LocatorKind.TestId => MatchesTestId(spec, node),
            LocatorKind.AltText => MatchesAltText(spec, node),
            LocatorKind.Title => MatchesTitle(spec, node),
            LocatorKind.Id => MatchesId(spec, node),
            _ => false,
        };
    }

    private static bool MatchesText(LocatorSpec spec, DomNode node)
    {
        string? own = ElementText(node);
        if (own is null || !TextNormalizer.Matches(own, spec.Value, spec.Exact))
        {
            return false;
        }

        // Playwright's text locator resolves to the smallest element containing the text.
        return !HasMatchingDescendant(node, spec);
    }

    private static bool HasMatchingDescendant(DomNode node, LocatorSpec spec)
    {
        foreach (DomNode child in node.TraversalChildren())
        {
            if (child.IsElement)
            {
                string? childText = ElementText(child);
                if (childText is not null && TextNormalizer.Matches(childText, spec.Value, spec.Exact))
                {
                    return true;
                }
            }

            if (HasMatchingDescendant(child, spec))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPlaceholder(LocatorSpec spec, DomNode node)
    {
        if (node.LocalName is not ("input" or "textarea"))
        {
            return false;
        }

        string? value = node.GetAttribute("placeholder");
        return value is not null && TextNormalizer.Matches(TextNormalizer.Normalize(value), spec.Value, spec.Exact);
    }

    private static bool MatchesTestId(LocatorSpec spec, DomNode node)
    {
        string? value = node.GetAttribute(Selectors.TestIdAttribute);
        return value is not null && value.Equals(spec.Value, StringComparison.Ordinal);
    }

    private static bool MatchesAltText(LocatorSpec spec, DomNode node)
    {
        if (node.LocalName is not ("img" or "area")
            && !(node.LocalName == "input"
                && node.GetAttribute("type")?.Equals("image", StringComparison.OrdinalIgnoreCase) == true))
        {
            return false;
        }

        string? value = node.GetAttribute("alt");
        return value is not null && TextNormalizer.Matches(TextNormalizer.Normalize(value), spec.Value, spec.Exact);
    }

    private static bool MatchesTitle(LocatorSpec spec, DomNode node)
    {
        string? value = node.GetAttribute("title");
        return value is not null && TextNormalizer.Matches(TextNormalizer.Normalize(value), spec.Value, spec.Exact);
    }

    private static bool MatchesId(LocatorSpec spec, DomNode node)
    {
        string? value = node.GetAttribute("id");
        return value is not null && value.Equals(spec.Value, StringComparison.Ordinal);
    }

    private static bool MatchesLabel(LocatorSpec spec, DomNode node, SemanticContext semantic)
    {
        if (!IsLabelable(node) && !IsContentEditable(node))
        {
            return false;
        }

        string? name = LabelText(node, semantic);
        return name is not null && TextNormalizer.Matches(name, spec.Value, spec.Exact);
    }

    private static bool MatchesRole(LocatorSpec spec, DomNode node, SemanticContext semantic)
    {
        string? role = ExplicitRole(node) ?? ImplicitRole(node);
        if (role is null || !role.Equals(spec.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchesRoleOptions(node, role, spec.Options))
        {
            return false;
        }

        if (spec.Name is null)
        {
            return true;
        }

        string? name = AccessibleName(node, semantic, allowContents: !NoTextContentRoles.Contains(role));
        return name is not null && TextNormalizer.Matches(name, spec.Name, spec.Exact);
    }

    private static bool MatchesRoleOptions(DomNode node, string role, RoleOptions? options)
    {
        if (options is null)
        {
            return true;
        }

        if (options.Level is { } level && RoleLevel(node, role) != level)
        {
            return false;
        }

        if (options.Checked is { } checkedState && ReadChecked(node) != checkedState)
        {
            return false;
        }

        if (options.Disabled is { } disabled && ReadDisabled(node) != disabled)
        {
            return false;
        }

        if (options.Expanded is { } expanded && ReadAriaBoolean(node, "aria-expanded") != expanded)
        {
            return false;
        }

        if (options.Pressed is { } pressed && ReadAriaBoolean(node, "aria-pressed") != pressed)
        {
            return false;
        }

        if (options.Selected is { } selected && ReadAriaBoolean(node, "aria-selected") != selected)
        {
            return false;
        }

        if (options.IncludeHidden != true && IsHiddenForAccessibility(node))
        {
            return false;
        }

        return true;
    }

    private static int RoleLevel(DomNode node, string role)
    {
        string? ariaLevel = node.GetAttribute("aria-level");
        if (ariaLevel is not null && int.TryParse(ariaLevel, out int parsed))
        {
            return parsed;
        }

        if (role.Equals("heading", StringComparison.OrdinalIgnoreCase)
            && node.LocalName.Length == 2
            && node.LocalName[0] == 'h'
            && node.LocalName[1] is >= '1' and <= '6')
        {
            return node.LocalName[1] - '0';
        }

        return 0;
    }

    private static bool ReadChecked(DomNode node)
    {
        string? aria = node.GetAttribute("aria-checked");
        if (aria is not null)
        {
            return aria.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return node.HasAttribute("checked");
    }

    private static bool ReadDisabled(DomNode node)
    {
        if (node.HasAttribute("disabled"))
        {
            return true;
        }

        string? aria = node.GetAttribute("aria-disabled");
        return aria is not null && aria.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadAriaBoolean(DomNode node, string attribute)
    {
        string? value = node.GetAttribute(attribute);
        return value switch
        {
            null => null,
            _ when value.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when value.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };
    }

    private static bool IsHiddenForAccessibility(DomNode node)
    {
        if (node.HasAttribute("hidden"))
        {
            return true;
        }

        string? ariaHidden = node.GetAttribute("aria-hidden");
        if (ariaHidden is not null && ariaHidden.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? style = node.GetAttribute("style");
        return style is not null
            && style.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Contains("display:none", StringComparison.OrdinalIgnoreCase);
    }

    private static string? LabelText(DomNode node, SemanticContext semantic)
    {
        string? labelledBy = node.GetAttribute("aria-labelledby");
        if (!string.IsNullOrEmpty(labelledBy))
        {
            StringBuilder builder = new();
            foreach (string labelledById in labelledBy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (semantic.ById.TryGetValue(labelledById, out DomNode? labelled) && !ReferenceEquals(labelled, node))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(labelled.TextContent());
                }
            }

            if (builder.Length > 0)
            {
                return TextNormalizer.Normalize(builder.ToString());
            }
        }

        string? ariaLabel = node.GetAttribute("aria-label");
        if (!string.IsNullOrWhiteSpace(ariaLabel))
        {
            return TextNormalizer.Normalize(ariaLabel);
        }

        string? id = node.GetAttribute("id");
        if (!string.IsNullOrEmpty(id) && semantic.LabelTextById.TryGetValue(id, out string? labelText))
        {
            return labelText;
        }

        for (DomNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.LocalName == "label")
            {
                return TextNormalizer.Normalize(parent.TextContent());
            }
        }

        return null;
    }

    private static string? AccessibleName(DomNode node, SemanticContext semantic, bool allowContents)
    {
        string? label = LabelText(node, semantic);
        if (label is not null)
        {
            return label;
        }

        if (node.LocalName == "img")
        {
            string? alt = node.GetAttribute("alt");
            if (!string.IsNullOrWhiteSpace(alt))
            {
                return TextNormalizer.Normalize(alt);
            }
        }

        if (allowContents)
        {
            string? text = ElementText(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        string? title = node.GetAttribute("title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            return TextNormalizer.Normalize(title);
        }

        string? placeholder = node.GetAttribute("placeholder");
        if (!string.IsNullOrWhiteSpace(placeholder))
        {
            return TextNormalizer.Normalize(placeholder);
        }

        return null;
    }

    private static string? ExplicitRole(DomNode node)
    {
        string? role = node.GetAttribute("role");
        return string.IsNullOrWhiteSpace(role) ? null : role.Trim();
    }

    private static string? ImplicitRole(DomNode node)
    {
        string tag = node.LocalName;
        return tag switch
        {
            "a" when node.HasAttribute("href") => "link",
            "button" => "button",
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
            "img" => "img",
            "input" => InputRole(node),
            "textarea" => "textbox",
            "select" => node.GetAttribute("multiple") is null ? "combobox" : "listbox",
            "option" => "option",
            "table" => "table",
            "tr" => "row",
            "td" => "cell",
            "th" => "columnheader",
            "ul" or "ol" => "list",
            "li" => "listitem",
            "nav" => "navigation",
            "main" => "main",
            "header" => "banner",
            "footer" => "contentinfo",
            "form" => "form",
            "article" => "article",
            "dialog" => "dialog",
            "p" => "paragraph",
            "hr" => "separator",
            "progress" => "progressbar",
            "summary" => "button",
            _ => IsContentEditable(node) ? "textbox" : null,
        };
    }

    private static string? InputRole(DomNode node)
    {
        string type = (node.GetAttribute("type") ?? "text").ToLowerInvariant();
        return type switch
        {
            "button" => "button",
            "submit" => "button",
            "reset" => "button",
            "checkbox" => "checkbox",
            "radio" => "radio",
            "range" => "slider",
            "number" => "spinbutton",
            "search" => "searchbox",
            "email" or "tel" or "url" or "text" or "password" => "textbox",
            _ => null,
        };
    }

    private static bool IsLabelable(DomNode node)
    {
        string tag = node.LocalName;
        return tag is "input" or "select" or "textarea" or "button" or "meter" or "output" or "progress";
    }

    private static bool IsContentEditable(DomNode node)
    {
        string? value = node.GetAttribute("contenteditable");
        return value is not null && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ElementText(DomNode node)
    {
        string tag = node.LocalName;
        if (tag is "script" or "style" or "template" or "noscript")
        {
            return null;
        }

        return TextNormalizer.Normalize(node.TextContent());
    }
}

internal static class TextNormalizer
{
    public static string Normalize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    public static bool Matches(string actual, string expected, bool exact)
        => exact
            ? actual.Equals(expected, StringComparison.Ordinal)
            : actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
}
