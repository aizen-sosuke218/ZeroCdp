using ZeroCdp.Dom;

namespace ZeroCdp.Selectors;

/// <summary>Evaluates compiled selectors against a DOM snapshot.</summary>
internal static class SelectorMatcher
{
    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\f'];

    public static bool MatchesAny(CompiledSelector[] selectors, DomNode node)
    {
        foreach (CompiledSelector selector in selectors)
        {
            if (Matches(selector, node))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(CompiledSelector selector, DomNode node)
    {
        if (!MatchesCompound(selector.Compounds[0], node))
        {
            return false;
        }

        return MatchAt(selector, index: 1, node);
    }

    private static bool MatchAt(CompiledSelector selector, int index, DomNode current)
    {
        if (index >= selector.Compounds.Length)
        {
            return true;
        }

        CompoundSelector target = selector.Compounds[index];
        switch (selector.Combinators[index - 1])
        {
            case Combinator.Child:
            {
                DomNode? parent = current.Parent;
                return parent is not null
                    && MatchesCompound(target, parent)
                    && MatchAt(selector, index + 1, parent);
            }

            case Combinator.Descendant:
            {
                DomNode? ancestor = current.Parent;
                while (ancestor is not null)
                {
                    if (MatchesCompound(target, ancestor) && MatchAt(selector, index + 1, ancestor))
                    {
                        return true;
                    }

                    ancestor = ancestor.Parent;
                }

                return false;
            }

            case Combinator.Adjacent:
            {
                DomNode? previous = current.PreviousElementSibling();
                return previous is not null
                    && MatchesCompound(target, previous)
                    && MatchAt(selector, index + 1, previous);
            }

            case Combinator.Sibling:
            {
                DomNode? previous = current.PreviousElementSibling();
                while (previous is not null)
                {
                    if (MatchesCompound(target, previous) && MatchAt(selector, index + 1, previous))
                    {
                        return true;
                    }

                    previous = previous.PreviousElementSibling();
                }

                return false;
            }

            default:
                return false;
        }
    }

    private static bool MatchesCompound(CompoundSelector compound, DomNode node)
    {
        if (!node.IsElement)
        {
            return false;
        }

        if (compound.TypeName is { } typeName
            && !node.LocalName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (compound.Id is { } id && node.GetAttribute("id") != id)
        {
            return false;
        }

        if (compound.Classes is { } classes)
        {
            string? classAttribute = node.GetAttribute("class");
            if (classAttribute is null)
            {
                return false;
            }

            foreach (string required in classes)
            {
                if (!HasClass(classAttribute, required))
                {
                    return false;
                }
            }
        }

        if (compound.Attributes is { } attributes)
        {
            foreach (AttributeSelector attribute in attributes)
            {
                if (!MatchesAttribute(attribute, node))
                {
                    return false;
                }
            }
        }

        if (compound.Pseudos is { } pseudos)
        {
            foreach (PseudoSelector pseudo in pseudos)
            {
                if (!MatchesPseudo(pseudo, node))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasClass(string classAttribute, string required)
    {
        ReadOnlySpan<char> remaining = classAttribute;
        while (!remaining.IsEmpty)
        {
            int index = remaining.IndexOfAny(Whitespace);
            ReadOnlySpan<char> token = index < 0 ? remaining : remaining[..index];
            if (token.Equals(required, StringComparison.Ordinal))
            {
                return true;
            }

            if (index < 0)
            {
                return false;
            }

            remaining = remaining[(index + 1)..];
        }

        return false;
    }

    private static bool MatchesAttribute(AttributeSelector selector, DomNode node)
    {
        string? actual = node.GetAttribute(selector.Name);
        if (actual is null)
        {
            return false;
        }

        if (selector.Operator == AttributeOperator.Exists)
        {
            return true;
        }

        StringComparison comparison = selector.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        ReadOnlySpan<char> expected = selector.Value.AsSpan();
        ReadOnlySpan<char> value = actual.AsSpan();

        return selector.Operator switch
        {
            AttributeOperator.Equals => value.Equals(expected, comparison),
            AttributeOperator.Includes => ContainsWord(value, expected, comparison),
            AttributeOperator.DashMatch => value.Equals(expected, comparison)
                || (value.Length > expected.Length
                    && value[expected.Length] == '-'
                    && value[..expected.Length].Equals(expected, comparison)),
            AttributeOperator.Prefix => value.StartsWith(expected, comparison),
            AttributeOperator.Suffix => value.EndsWith(expected, comparison),
            AttributeOperator.Substring => value.Contains(expected, comparison),
            _ => false,
        };
    }

    private static bool ContainsWord(ReadOnlySpan<char> value, ReadOnlySpan<char> expected, StringComparison comparison)
    {
        while (!value.IsEmpty)
        {
            int index = value.IndexOfAny(Whitespace);
            ReadOnlySpan<char> token = index < 0 ? value : value[..index];
            if (token.Equals(expected, comparison))
            {
                return true;
            }

            if (index < 0)
            {
                return false;
            }

            value = value[(index + 1)..];
        }

        return false;
    }

    private static bool MatchesPseudo(PseudoSelector pseudo, DomNode node)
    {
        switch (pseudo.Kind)
        {
            case PseudoClass.FirstChild:
                return node.ElementIndex() == 1;

            case PseudoClass.LastChild:
                return node.ElementIndex() == node.ElementCount();

            case PseudoClass.OnlyChild:
                return node.ElementCount() == 1;

            case PseudoClass.NthChild:
                return MatchesNth(pseudo, node.ElementIndex());

            case PseudoClass.NthLastChild:
                return MatchesNth(pseudo, node.ElementCount() - node.ElementIndex() + 1);

            case PseudoClass.FirstOfType:
                return TypeIndex(node) == 1;

            case PseudoClass.LastOfType:
                return TypeIndex(node) == TypeCount(node);

            case PseudoClass.NthOfType:
                return MatchesNth(pseudo, TypeIndex(node));

            case PseudoClass.Not:
                return pseudo.Arguments is not null && !MatchesAny(pseudo.Arguments, node);

            case PseudoClass.Is:
                return pseudo.Arguments is not null && MatchesAny(pseudo.Arguments, node);

            case PseudoClass.Root:
                return node.Parent is { NodeType: DomNode.DocumentNode };

            case PseudoClass.Empty:
                return IsEmpty(node);

            case PseudoClass.Checked:
                return node.HasAttribute("checked") || node.HasAttribute("selected");

            case PseudoClass.Disabled:
                return node.HasAttribute("disabled");

            case PseudoClass.Enabled:
                return !node.HasAttribute("disabled");

            case PseudoClass.HasText:
                return pseudo.Text is { } expected
                    && node.TextContent().Contains(expected, StringComparison.OrdinalIgnoreCase);

            case PseudoClass.Visible:
                return IsLikelyVisible(node);

            default:
                return false;
        }
    }

    /// <summary>
    /// A snapshot carries no layout, so visibility is approximated from markup: an element is
    /// considered visible unless it is <c>hidden</c>, <c>aria-hidden="true"</c>, or carries an
    /// inline <c>display:none</c>/<c>visibility:hidden</c>. CSS-sheet rules are not evaluated.
    /// </summary>
    private static bool IsLikelyVisible(DomNode node)
    {
        if (node.HasAttribute("hidden"))
        {
            return false;
        }

        string? ariaHidden = node.GetAttribute("aria-hidden");
        if (ariaHidden is not null && ariaHidden.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? style = node.GetAttribute("style");
        if (style is not null)
        {
            string compact = style.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (compact.Contains("display:none", StringComparison.OrdinalIgnoreCase)
                || compact.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesNth(PseudoSelector pseudo, int position)
    {
        if (position <= 0)
        {
            return false;
        }

        if (pseudo.Step == 0)
        {
            return position == pseudo.Offset;
        }

        int delta = position - pseudo.Offset;
        return delta % pseudo.Step == 0 && delta / pseudo.Step >= 0;
    }

    private static bool IsEmpty(DomNode node)
    {
        if (node.Children is null)
        {
            return true;
        }

        foreach (DomNode child in node.Children)
        {
            if (child.IsElement)
            {
                return false;
            }

            if (child.NodeType == DomNode.TextNode && child.NodeValue is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
        }

        return true;
    }

    private static int TypeIndex(DomNode node)
    {
        DomNode? parent = node.Parent;
        if (parent?.Children is null)
        {
            return 1;
        }

        int index = 0;
        foreach (DomNode sibling in parent.Children)
        {
            if (sibling.IsElement && sibling.LocalName.Equals(node.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            if (ReferenceEquals(sibling, node))
            {
                return index;
            }
        }

        return index;
    }

    private static int TypeCount(DomNode node)
    {
        DomNode? parent = node.Parent;
        if (parent?.Children is null)
        {
            return 1;
        }

        int count = 0;
        foreach (DomNode sibling in parent.Children)
        {
            if (sibling.IsElement && sibling.LocalName.Equals(node.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }
}
