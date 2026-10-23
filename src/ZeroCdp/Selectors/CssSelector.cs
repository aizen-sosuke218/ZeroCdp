namespace ZeroCdp.Selectors;

/// <summary>Thrown when a selector cannot be parsed.</summary>
public sealed class SelectorSyntaxException : CdpException
{
    public SelectorSyntaxException(string selector, int position, string reason)
        : base($"Invalid selector at position {position}: {reason} (selector: '{selector}')")
    {
        Selector = selector;
        Position = position;
    }

    public string Selector { get; }

    public int Position { get; }
}

internal enum Combinator
{
    /// <summary>Descendant: whitespace.</summary>
    Descendant = 0,

    /// <summary>Direct child: <c>&gt;</c>.</summary>
    Child = 1,

    /// <summary>Adjacent sibling: <c>+</c>.</summary>
    Adjacent = 2,

    /// <summary>General sibling: <c>~</c>.</summary>
    Sibling = 3,
}

internal enum AttributeOperator
{
    Exists = 0,
    Equals = 1,
    Includes = 2,   // ~=
    DashMatch = 3,  // |=
    Prefix = 4,     // ^=
    Suffix = 5,     // $=
    Substring = 6,  // *=
}

internal enum PseudoClass
{
    FirstChild,
    LastChild,
    OnlyChild,
    NthChild,
    NthLastChild,
    FirstOfType,
    LastOfType,
    NthOfType,
    Not,
    Is,
    Root,
    Empty,
    Checked,
    Disabled,
    Enabled,

    /// <summary><c>:has-text("…")</c>, matched against the snapshot's text content.</summary>
    HasText,

    /// <summary><c>:visible</c>, approximated from the snapshot (no layout information).</summary>
    Visible,
}

/// <summary>One compound selector such as <c>div.card[data-x="1"]:first-child</c>.</summary>
internal sealed class CompoundSelector
{
    public string? TypeName { get; set; }

    public string? Id { get; set; }

    public List<string>? Classes { get; set; }

    public List<AttributeSelector>? Attributes { get; set; }

    public List<PseudoSelector>? Pseudos { get; set; }
}

internal sealed class AttributeSelector
{
    public required string Name { get; init; }

    public AttributeOperator Operator { get; init; }

    public string Value { get; init; } = string.Empty;

    public bool IgnoreCase { get; init; }
}

internal sealed class PseudoSelector
{
    public PseudoClass Kind { get; init; }

    /// <summary>For <c>nth-*</c>: the <c>a</c> of <c>an+b</c>.</summary>
    public int Step { get; init; }

    /// <summary>For <c>nth-*</c>: the <c>b</c> of <c>an+b</c>.</summary>
    public int Offset { get; init; }

    /// <summary>For <c>:not()</c> / <c>:is()</c>.</summary>
    public CompiledSelector[]? Arguments { get; init; }

    /// <summary>For <c>:has-text()</c>.</summary>
    public string? Text { get; init; }
}

/// <summary>A selector list (<c>a, b</c>) or one complex selector.</summary>
internal sealed class CompiledSelector
{
    /// <summary>Compounds ordered right-to-left, matching how the engine evaluates them.</summary>
    public required CompoundSelector[] Compounds { get; init; }

    /// <summary><c>Combinators[i]</c> relates <c>Compounds[i]</c> to <c>Compounds[i + 1]</c>.</summary>
    public required Combinator[] Combinators { get; init; }
}

/// <summary>Parses the CSS selector subset ZeroCDP evaluates in managed code.</summary>
/// <remarks>
/// The subset exists so a selector can be matched against a <c>pierced</c> DOM snapshot, which is
/// what makes closed shadow roots reachable without injecting anything into the page.
/// </remarks>
internal static class CssSelectorParser
{
    public static CompiledSelector[] ParseList(string selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Length == 0)
        {
            throw new SelectorSyntaxException(selector, 0, "the selector is empty");
        }

        List<CompiledSelector> results = new();
        int start = 0;
        int depth = 0;

        // Only brackets and parentheses affect nesting: a quote always lives inside an attribute
        // selector, so counting it would leave the depth unbalanced.
        for (int i = 0; i < selector.Length; i++)
        {
            char c = selector[i];
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                results.Add(ParsePart(selector, start, i));
                start = i + 1;
            }
        }

        // The tail is always a selector, so an unterminated bracket is reported instead of being
        // silently dropped.
        results.Add(ParsePart(selector, start, selector.Length));
        return results.ToArray();
    }

    private static CompiledSelector ParsePart(string selector, int start, int end)
    {
        string part = selector[start..end].Trim();
        if (part.Length == 0)
        {
            throw new SelectorSyntaxException(selector, start, "empty selector in list");
        }

        return ParseComplex(selector, part);
    }

    private static CompiledSelector ParseComplex(string original, string text)
    {
        List<CompoundSelector> compounds = new();
        List<Combinator> combinators = new();

        int i = 0;
        SkipWhitespace(text, ref i);

        while (i < text.Length)
        {
            compounds.Add(ParseCompound(original, text, ref i));

            bool hadWhitespace = SkipWhitespace(text, ref i);
            if (i >= text.Length)
            {
                break;
            }

            char c = text[i];
            if (c is '>' or '+' or '~')
            {
                combinators.Add(c switch
                {
                    '>' => Combinator.Child,
                    '+' => Combinator.Adjacent,
                    _ => Combinator.Sibling,
                });

                i++;
                SkipWhitespace(text, ref i);
            }
            else if (hadWhitespace)
            {
                combinators.Add(Combinator.Descendant);
            }
            else
            {
                throw new SelectorSyntaxException(original, i, $"unexpected '{c}'");
            }
        }

        if (compounds.Count == 0)
        {
            throw new SelectorSyntaxException(original, 0, "no compound selector");
        }

        if (combinators.Count != compounds.Count - 1)
        {
            throw new SelectorSyntaxException(original, text.Length, "trailing combinator");
        }

        // Flip to right-to-left so matching starts at the element being tested.
        CompoundSelector[] reversed = new CompoundSelector[compounds.Count];
        for (int k = 0; k < compounds.Count; k++)
        {
            reversed[k] = compounds[compounds.Count - 1 - k];
        }

        Combinator[] reversedCombinators = new Combinator[combinators.Count];
        for (int k = 0; k < combinators.Count; k++)
        {
            reversedCombinators[k] = combinators[combinators.Count - 1 - k];
        }

        return new CompiledSelector { Compounds = reversed, Combinators = reversedCombinators };
    }

    private static CompoundSelector ParseCompound(string original, string text, ref int i)
    {
        CompoundSelector compound = new();
        int start = i;

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetter(c) || c == '-' || c == '_' || c == '*' || c == '|')
            {
                if (compound.TypeName is not null || compound.Id is not null || compound.Classes is not null)
                {
                    // A second type selector with no combinator is a syntax error.
                    if (c != '*' || compound.TypeName is not null)
                    {
                        throw new SelectorSyntaxException(original, i, "unexpected type selector");
                    }
                }

                int nameStart = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_' or '*'))
                {
                    i++;
                }

                string name = text[nameStart..i];
                compound.TypeName = name == "*" ? null : name.ToLowerInvariant();
                continue;
            }

            if (c == '#')
            {
                i++;
                compound.Id = ReadIdentifier(original, text, ref i);
                continue;
            }

            if (c == '.')
            {
                i++;
                (compound.Classes ??= []).Add(ReadIdentifier(original, text, ref i));
                continue;
            }

            if (c == '[')
            {
                (compound.Attributes ??= []).Add(ParseAttribute(original, text, ref i));
                continue;
            }

            if (c == ':')
            {
                (compound.Pseudos ??= []).Add(ParsePseudo(original, text, ref i));
                continue;
            }

            break;
        }

        if (i == start)
        {
            throw new SelectorSyntaxException(original, i, $"unexpected '{text[i]}'");
        }

        return compound;
    }

    private static AttributeSelector ParseAttribute(string original, string text, ref int i)
    {
        i++; // '['
        SkipWhitespace(text, ref i);

        string name = ReadIdentifier(original, text, ref i);
        SkipWhitespace(text, ref i);

        if (i >= text.Length)
        {
            throw new SelectorSyntaxException(original, i, "unterminated attribute selector");
        }

        if (text[i] == ']')
        {
            i++;
            return new AttributeSelector { Name = name };
        }

        AttributeOperator op;
        char c = text[i];
        if (c == '=')
        {
            op = AttributeOperator.Equals;
            i++;
        }
        else
        {
            if (i + 1 >= text.Length || text[i + 1] != '=')
            {
                throw new SelectorSyntaxException(original, i, "expected '='");
            }

            op = c switch
            {
                '~' => AttributeOperator.Includes,
                '|' => AttributeOperator.DashMatch,
                '^' => AttributeOperator.Prefix,
                '$' => AttributeOperator.Suffix,
                '*' => AttributeOperator.Substring,
                _ => throw new SelectorSyntaxException(original, i, $"unsupported operator '{c}'"),
            };

            i += 2;
        }

        SkipWhitespace(text, ref i);
        string value = ReadValue(original, text, ref i);
        SkipWhitespace(text, ref i);

        bool ignoreCase = false;
        if (i < text.Length && (text[i] == 'i' || text[i] == 'I'))
        {
            ignoreCase = true;
            i++;
            SkipWhitespace(text, ref i);
        }
        else if (i < text.Length && (text[i] == 's' || text[i] == 'S'))
        {
            i++;
            SkipWhitespace(text, ref i);
        }

        if (i >= text.Length || text[i] != ']')
        {
            throw new SelectorSyntaxException(original, i, "expected ']'");
        }

        i++;
        return new AttributeSelector
        {
            Name = name,
            Operator = op,
            Value = value,
            IgnoreCase = ignoreCase,
        };
    }

    private static PseudoSelector ParsePseudo(string original, string text, ref int i)
    {
        i++; // ':'
        if (i < text.Length && text[i] == ':')
        {
            throw new SelectorSyntaxException(original, i, "pseudo-elements cannot be matched");
        }

        string name = ReadIdentifier(original, text, ref i).ToLowerInvariant();

        if (i < text.Length && text[i] == '(')
        {
            int argumentStart = ++i;
            int depth = 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                i++;
            }

            if (depth != 0)
            {
                throw new SelectorSyntaxException(original, i, "unterminated pseudo-class argument");
            }

            string argument = text[argumentStart..i].Trim();
            i++; // ')'

            return name switch
            {
                "nth-child" => Nth(PseudoClass.NthChild, original, argument),
                "nth-last-child" => Nth(PseudoClass.NthLastChild, original, argument),
                "nth-of-type" => Nth(PseudoClass.NthOfType, original, argument),
                "not" => new PseudoSelector { Kind = PseudoClass.Not, Arguments = ParseList(argument) },
                "is" or "where" => new PseudoSelector { Kind = PseudoClass.Is, Arguments = ParseList(argument) },
                "has-text" => new PseudoSelector
                {
                    Kind = PseudoClass.HasText,
                    Text = Unquote(argument),
                },
                _ => throw new SelectorSyntaxException(original, i, $"unsupported pseudo-class ':{name}()'"),
            };
        }

        return name switch
        {
            "first-child" => new PseudoSelector { Kind = PseudoClass.FirstChild },
            "last-child" => new PseudoSelector { Kind = PseudoClass.LastChild },
            "only-child" => new PseudoSelector { Kind = PseudoClass.OnlyChild },
            "first-of-type" => new PseudoSelector { Kind = PseudoClass.FirstOfType },
            "last-of-type" => new PseudoSelector { Kind = PseudoClass.LastOfType },
            "root" => new PseudoSelector { Kind = PseudoClass.Root },
            "empty" => new PseudoSelector { Kind = PseudoClass.Empty },
            "checked" => new PseudoSelector { Kind = PseudoClass.Checked },
            "disabled" => new PseudoSelector { Kind = PseudoClass.Disabled },
            "enabled" => new PseudoSelector { Kind = PseudoClass.Enabled },
            "visible" => new PseudoSelector { Kind = PseudoClass.Visible },
            _ => throw new SelectorSyntaxException(original, i, $"unsupported pseudo-class ':{name}'"),
        };
    }

    private static string Unquote(string argument)
    {
        string value = argument.Trim();
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static PseudoSelector Nth(PseudoClass kind, string original, string argument)
    {
        (int step, int offset) = ParseAnPlusB(original, argument);
        return new PseudoSelector { Kind = kind, Step = step, Offset = offset };
    }

    /// <summary>Parses <c>an+b</c>, <c>odd</c>, <c>even</c>, or a bare index.</summary>
    private static (int Step, int Offset) ParseAnPlusB(string original, string text)
    {
        string value = text.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (value.Length == 0)
        {
            throw new SelectorSyntaxException(original, 0, "empty nth expression");
        }

        if (value == "odd")
        {
            return (2, 1);
        }

        if (value == "even")
        {
            return (2, 0);
        }

        int nIndex = value.IndexOf('n');
        if (nIndex < 0)
        {
            return (0, ParseInt(value));
        }

        string stepPart = value[..nIndex];
        int step = stepPart switch
        {
            "" or "+" => 1,
            "-" => -1,
            _ => ParseInt(stepPart),
        };

        string offsetPart = value[(nIndex + 1)..];
        int offset = offsetPart.Length == 0 ? 0 : ParseInt(offsetPart);
        return (step, offset);
    }

    private static int ParseInt(string text)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int value))
        {
            throw new SelectorSyntaxException(text, 0, $"'{text}' is not a number");
        }

        return value;
    }

    private static string ReadIdentifier(string original, string text, ref int i)
    {
        int start = i;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '\\')
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            break;
        }

        if (i == start)
        {
            throw new SelectorSyntaxException(original, i, "expected an identifier");
        }

        return text[start..i];
    }

    private static string ReadValue(string original, string text, ref int i)
    {
        if (i < text.Length && (text[i] == '"' || text[i] == '\''))
        {
            char quote = text[i++];
            int start = i;
            while (i < text.Length && text[i] != quote)
            {
                i++;
            }

            if (i >= text.Length)
            {
                throw new SelectorSyntaxException(original, i, "unterminated string");
            }

            string value = text[start..i];
            i++;
            return value;
        }

        return ReadIdentifier(original, text, ref i);
    }

    private static bool SkipWhitespace(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i > start;
    }
}
