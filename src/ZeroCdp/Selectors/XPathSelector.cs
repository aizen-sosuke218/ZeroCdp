using System.Globalization;
using ZeroCdp.Dom;

namespace ZeroCdp.Selectors;

/// <summary>
/// A compiled XPath expression evaluated against a pierced DOM snapshot.
/// </summary>
/// <remarks>
/// This exists for one reason: closed shadow roots. <c>DOM.performSearch</c> cannot see inside
/// them, while <c>DOM.getDocument(pierce:true)</c> can, so XPath support is implemented against
/// the snapshot tree that already powers the CSS fallback and the semantic locators. Nothing is
/// injected into the page.
/// <para>
/// The supported subset is deliberate rather than accidental: child, descendant,
/// descendant-or-self, self and parent axes; <c>*</c>, name, <c>text()</c> and <c>node()</c> tests;
/// positional predicates; attribute existence/comparison; <c>contains</c>, <c>starts-with</c>,
/// <c>normalize-space</c>, <c>not</c>, <c>and</c> and <c>or</c>. Anything outside it throws
/// <see cref="SelectorSyntaxException"/> so a caller never gets a silently wrong result.
/// </para>
/// </remarks>
internal sealed class XPathSelector
{
    private readonly XPathStep[] _steps;
    private readonly bool _absolute;

    private XPathSelector(XPathStep[] steps, bool absolute)
    {
        _steps = steps;
        _absolute = absolute;
    }

    /// <summary>The original expression, used in diagnostics.</summary>
    public string Display { get; private init; } = string.Empty;

    public static XPathSelector Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string text = expression.Trim();
        if (text.Length == 0)
        {
            throw new SelectorSyntaxException(expression, 0, "the XPath expression is empty");
        }

        int i = 0;
        bool absolute = false;
        if (text[i] == '/')
        {
            absolute = true;
        }

        List<XPathStep> steps = new();
        while (i < text.Length)
        {
            bool descendant = false;
            if (text[i] == '/')
            {
                i++;
                if (i < text.Length && text[i] == '/')
                {
                    descendant = true;
                    i++;
                }
            }
            else if (steps.Count > 0)
            {
                throw new SelectorSyntaxException(expression, i, $"expected '/' but found '{text[i]}'");
            }

            SkipWhitespace(text, ref i);
            if (i >= text.Length)
            {
                throw new SelectorSyntaxException(expression, i, "the XPath expression ends with '/'");
            }

            XPathStep step = ParseStep(expression, text, ref i, steps.Count == 0);
            if (descendant)
            {
                // '//' is a shorthand for /descendant-or-self::node()/.
                steps.Add(new XPathStep
                {
                    Axis = XPathAxis.DescendantOrSelf,
                    NodeTest = XPathNodeTest.AnyNode,
                });
            }

            steps.Add(step);
            SkipWhitespace(text, ref i);
        }

        if (steps.Count == 0 || text[^1] == '/')
        {
            throw new SelectorSyntaxException(expression, text.Length, "no path step");
        }

        return new XPathSelector(steps.ToArray(), absolute) { Display = expression };
    }

    /// <summary>
    /// Evaluates the expression relative to <paramref name="root"/> as the context node.
    /// </summary>
    /// <remarks>
    /// A leading <c>/</c> is still scoped to <paramref name="root"/>: Playwright behaves the same
    /// way for <c>locator.locator('//x')</c>, where the path is relative to the parent locator.
    /// </remarks>
    public long[] ResolveUnder(DomNode root)
        => Resolve(root, contextOnly: true);

    /// <summary>Evaluates the expression with the document containing <paramref name="root"/> as context.</summary>
    public long[] Resolve(DomNode root)
        => Resolve(root, contextOnly: false);

    private long[] Resolve(DomNode root, bool contextOnly)
    {
    
        // The scope document is the nearest document ancestor, not the top-most one: a frame
        // document lives in the same snapshot tree as its parent page.
        DomNode document = root;
        while (document.NodeType != DomNode.DocumentNode && document.Parent is not null)
        {
            document = document.Parent;
        }

        Dictionary<DomNode, int> order = BuildOrder(document);
        List<DomNode> current = !contextOnly && (_absolute || ReferenceEquals(root, document))
            ? [document]
            : [root];

        foreach (XPathStep step in _steps)
        {
            HashSet<DomNode> seen = new(ReferenceEqualityComparer.Instance);
            List<DomNode> candidates = new();

            foreach (DomNode context in current)
            {
                foreach (DomNode candidate in Select(step.Axis, context))
                {
                    if (ReferenceEquals(candidate, context) && step.Axis is XPathAxis.Descendant)
                    {
                        continue;
                    }

                    if (!step.Matches(candidate))
                    {
                        continue;
                    }

                    if (seen.Add(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            // XPath positions are document order, so sort before applying predicates.
            candidates.Sort((left, right) =>
            {
                int leftRank = order.TryGetValue(left, out int l) ? l : int.MaxValue;
                int rightRank = order.TryGetValue(right, out int r) ? r : int.MaxValue;
                return leftRank.CompareTo(rightRank);
            });

            for (int p = 0; p < step.Predicates.Count; p++)
            {
                List<DomNode> filtered = new(candidates.Count);
                int size = candidates.Count;
                for (int c = 0; c < candidates.Count; c++)
                {
                    if (step.Predicates[p].Matches(candidates[c], position: c + 1, size))
                    {
                        filtered.Add(candidates[c]);
                    }
                }

                candidates = filtered;
            }

            current = candidates;
            if (current.Count == 0)
            {
                break;
            }
        }

        List<long> ids = new(current.Count);
        foreach (DomNode node in current)
        {
            if (node.IsElement && node.NodeId != 0)
            {
                ids.Add(node.NodeId);
            }
        }

        return ids.ToArray();
    }

    private static IEnumerable<DomNode> Select(XPathAxis axis, DomNode context)
    {
        switch (axis)
        {
            case XPathAxis.Self:
                yield return context;
                break;

            case XPathAxis.Parent:
                if (context.Parent is { } parent)
                {
                    yield return parent;
                }

                break;

            case XPathAxis.Descendant:
            case XPathAxis.DescendantOrSelf:
                if (axis == XPathAxis.DescendantOrSelf)
                {
                    yield return context;
                }

                foreach (DomNode node in Descendants(context))
                {
                    yield return node;
                }

                break;

            default:
                foreach (DomNode child in context.TraversalChildren())
                {
                    yield return child;
                }

                break;
        }
    }

    /// <summary>Depth-first descendants, refusing to cross into a nested document.</summary>
    private static IEnumerable<DomNode> Descendants(DomNode node)
    {
        Stack<DomNode> pending = new();
        pending.Push(node);

        while (pending.Count > 0)
        {
            DomNode current = pending.Pop();
            foreach (DomNode child in current.TraversalChildren())
            {
                if (child.NodeType == DomNode.DocumentNode)
                {
                    continue;
                }

                yield return child;
                pending.Push(child);
            }
        }
    }

    private static Dictionary<DomNode, int> BuildOrder(DomNode document)
    {
        Dictionary<DomNode, int> order = new(ReferenceEqualityComparer.Instance);
        Stack<DomNode> pending = new();
        pending.Push(document);
        int index = 0;

        // Pre-order with reverse pushes so the numbering matches document order.
        List<DomNode> children = new();
        while (pending.Count > 0)
        {
            DomNode node = pending.Pop();
            order[node] = index++;

            children.Clear();
            foreach (DomNode child in node.TraversalChildren())
            {
                if (child.NodeType == DomNode.DocumentNode && !ReferenceEquals(node, document))
                {
                    continue;
                }

                children.Add(child);
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        return order;
    }

    private static XPathStep ParseStep(string original, string text, ref int i, bool first)
    {
        XPathStep step = new();

        if (text[i] == '.')
        {
            if (i + 1 < text.Length && text[i + 1] == '.')
            {
                i += 2;
                step.Axis = XPathAxis.Parent;
                step.NodeTest = XPathNodeTest.AnyNode;
            }
            else
            {
                i++;
                step.Axis = XPathAxis.Self;
                step.NodeTest = XPathNodeTest.AnyNode;
            }

            ParsePredicates(original, text, ref i, step);
            return step;
        }

        // axis::test
        int save = i;
        int nameEnd = i;
        while (nameEnd < text.Length && (char.IsLetter(text[nameEnd]) || text[nameEnd] == '-'))
        {
            nameEnd++;
        }

        if (nameEnd + 1 < text.Length && text[nameEnd] == ':' && text[nameEnd + 1] == ':')
        {
            string axisName = text[i..nameEnd];
            i = nameEnd + 2;
            step.Axis = axisName switch
            {
                "child" => XPathAxis.Child,
                "descendant" => XPathAxis.Descendant,
                "descendant-or-self" => XPathAxis.DescendantOrSelf,
                "self" => XPathAxis.Self,
                "parent" => XPathAxis.Parent,
                _ => throw new SelectorSyntaxException(original, save, $"unsupported axis '{axisName}'"),
            };
        }
        else
        {
            i = save;
            // A leading '//' already emitted a descendant-or-self step.
            step.Axis = XPathAxis.Child;
        }

        SkipWhitespace(text, ref i);
        step.NodeTest = ParseNodeTest(original, text, ref i);
        ParsePredicates(original, text, ref i, step);
        return step;
    }

    private static XPathNodeTest ParseNodeTest(string original, string text, ref int i)
    {
        if (text[i] == '*')
        {
            i++;
            return XPathNodeTest.AnyElement;
        }

        int start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_' or ':' or '.'))
        {
            i++;
        }

        if (i == start)
        {
            throw new SelectorSyntaxException(original, i, $"unexpected '{text[i]}' in XPath");
        }

        string name = text[start..i];
        if (i < text.Length && text[i] == '(')
        {
            int close = text.IndexOf(')', i);
            if (close < 0)
            {
                throw new SelectorSyntaxException(original, i, "unterminated XPath function call");
            }

            i = close + 1;
            return name switch
            {
                "text" => XPathNodeTest.Text,
                "node" => XPathNodeTest.AnyNode,
                _ => throw new SelectorSyntaxException(original, start, $"unsupported node test '{name}()'"),
            };
        }

        return new XPathNodeTest(name);
    }

    private static void ParsePredicates(string original, string text, ref int i, XPathStep step)
    {
        while (true)
        {
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '[')
            {
                return;
            }

            int start = ++i;
            int depth = 1;
            char quote = '\0';
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
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
                throw new SelectorSyntaxException(original, i, "unterminated XPath predicate");
            }

            string predicate = text[start..i];
            i++; // ']'
            step.Predicates.Add(XPathPredicateParser.Parse(original, predicate));
        }
    }

    private static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }
}

internal enum XPathAxis
{
    Child,
    Descendant,
    DescendantOrSelf,
    Self,
    Parent,
}

internal readonly struct XPathNodeTest
{
    private readonly string? _name;

    public XPathNodeTest(string name) => _name = name;

    public string? Name => _name;

    public bool IsAnyElement => _name is null;

    public bool IsText { get; init; }

    public bool IsAnyNode { get; init; }

    public static XPathNodeTest AnyElement => default;

    public static XPathNodeTest Text => new() { IsText = true };

    public static XPathNodeTest AnyNode => new() { IsAnyNode = true };
}

internal sealed class XPathStep
{
    public XPathAxis Axis { get; set; } = XPathAxis.Child;

    public XPathNodeTest NodeTest { get; set; } = XPathNodeTest.AnyElement;

    public List<XPathPredicate> Predicates { get; } = new();

    public bool Matches(DomNode node)
    {
        if (NodeTest.IsAnyNode)
        {
            return true;
        }

        if (NodeTest.IsText)
        {
            return node.NodeType == DomNode.TextNode;
        }

        if (!node.IsElement)
        {
            return false;
        }

        return NodeTest.Name is not { } name
            || node.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (name.Contains(':') && node.NodeName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}

internal abstract class XPathPredicate
{
    public abstract bool Matches(DomNode context, int position, int size);
}

internal sealed class XPathPredicateParser
{
    private readonly string _original;
    private readonly string _text;
    private int _index;

    private XPathPredicateParser(string original, string text)
    {
        _original = original;
        _text = text;
    }

    public static XPathPredicate Parse(string original, string text)
    {
        XPathPredicateParser parser = new(original, text);
        XPathPredicate predicate = parser.ParseOr();
        parser.SkipWhitespace();
        if (parser._index < parser._text.Length)
        {
            throw new SelectorSyntaxException(original, parser._index, $"unexpected '{parser._text[parser._index]}' in XPath predicate");
        }

        return predicate;
    }

    private XPathPredicate ParseOr()
    {
        XPathPredicate left = ParseAnd();
        while (TryWord("or"))
        {
            XPathPredicate right = ParseAnd();
            left = new XPathLogicalPredicate(left, right, isOr: true);
        }

        return left;
    }

    private XPathPredicate ParseAnd()
    {
        XPathPredicate left = ParseNot();
        while (TryWord("and"))
        {
            XPathPredicate right = ParseNot();
            left = new XPathLogicalPredicate(left, right, isOr: false);
        }

        return left;
    }

    private XPathPredicate ParseNot()
    {
        SkipWhitespace();
        if (PeekWord("not") && PeekCharAfterWord() == '(')
        {
            Advance(3);
            Expect('(');
            XPathPredicate inner = ParseOr();
            Expect(')');
            return new XPathNotPredicate(inner);
        }

        return ParseComparison();
    }

    private XPathPredicate ParseComparison()
    {
        SkipWhitespace();
        XPathValue left = ParseValue();
        SkipWhitespace();

        string? op = null;
        if (TryOperator("!="))
        {
            op = "!=";
        }
        else if (TryOperator("="))
        {
            op = "=";
        }

        if (op is null)
        {
            return new XPathValuePredicate(left);
        }

        XPathValue right = ParseValue();
        return new XPathComparisonPredicate(left, right, op);
    }

    private XPathValue ParseValue()
    {
        SkipWhitespace();
        if (_index >= _text.Length)
        {
            throw Error("expected a value");
        }

        char c = _text[_index];
        if (c is '\'' or '"')
        {
            return new XPathValue(XPathValueKind.String, ReadLiteral());
        }

        if (char.IsDigit(c) || (c == '-' && _index + 1 < _text.Length && char.IsDigit(_text[_index + 1])))
        {
            return XPathValue.Number(ReadNumber());
        }

        if (c == '.')
        {
            _index++;
            return new XPathValue(XPathValueKind.TextContent, string.Empty);
        }

        if (c == '@')
        {
            _index++;
            return new XPathValue(XPathValueKind.Attribute, ReadName());
        }

        string function = ReadName();
        if (function.Length == 0)
        {
            throw Error("expected a value");
        }

        if (_index < _text.Length && _text[_index] == '(')
        {
            _index++;
            List<XPathValue> arguments = new();
            SkipWhitespace();
            if (_index < _text.Length && _text[_index] != ')')
            {
                while (true)
                {
                    arguments.Add(ParseValue());
                    SkipWhitespace();
                    if (_index < _text.Length && _text[_index] == ',')
                    {
                        _index++;
                        continue;
                    }

                    break;
                }
            }

            Expect(')');
            return new XPathValue(XPathValueKind.Function, function, arguments.ToArray());
        }

        throw Error($"unsupported XPath token '{function}'");
    }

    private string ReadLiteral()
    {
        char quote = _text[_index++];
        int start = _index;
        while (_index < _text.Length && _text[_index] != quote)
        {
            _index++;
        }

        if (_index >= _text.Length)
        {
            throw Error("unterminated string literal");
        }

        string value = _text[start.._index];
        _index++;
        return value;
    }

    private double ReadNumber()
    {
        int start = _index;
        while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] is '-' or '.'))
        {
            _index++;
        }

        return double.TryParse(_text[start.._index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw Error("invalid number");
    }

    private string ReadName()
    {
        int start = _index;
        while (_index < _text.Length
            && (char.IsLetterOrDigit(_text[_index]) || _text[_index] is '-' or '_' or ':' or '.'))
        {
            _index++;
        }

        return _text[start.._index];
    }

    private bool TryWord(string word)
    {
        SkipWhitespace();
        if (!PeekWord(word))
        {
            return false;
        }

        int end = _index + word.Length;
        if (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] == '_'))
        {
            return false;
        }

        _index = end;
        return true;
    }

    private bool PeekWord(string word)
        => _index + word.Length <= _text.Length
            && _text.AsSpan(_index, word.Length).Equals(word, StringComparison.Ordinal)
            && _index < _text.Length;

    private char PeekCharAfterWord()
    {
        int probe = _index;
        while (probe < _text.Length && char.IsLetter(_text[probe]))
        {
            probe++;
        }

        return probe < _text.Length ? _text[probe] : '\0';
    }

    private bool TryOperator(string op)
    {
        if (_index + op.Length <= _text.Length && _text.AsSpan(_index, op.Length).Equals(op, StringComparison.Ordinal))
        {
            _index += op.Length;
            return true;
        }

        return false;
    }

    private void Advance(int count) => _index += count;

    private void Expect(char expected)
    {
        SkipWhitespace();
        if (_index >= _text.Length || _text[_index] != expected)
        {
            throw Error($"expected '{expected}'");
        }

        _index++;
    }

    private void SkipWhitespace()
    {
        while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
        {
            _index++;
        }
    }

    private SelectorSyntaxException Error(string reason)
        => new(_original, _index, reason);
}

internal enum XPathValueKind
{
    String,
    Number,
    Attribute,
    TextContent,
    Function,
}

internal sealed class XPathValue
{
    private readonly double _number;
    private readonly string _text;
    private readonly XPathValue[] _arguments;

    public XPathValue(XPathValueKind kind, string text, XPathValue[]? arguments = null, double number = 0)
    {
        Kind = kind;
        _text = text;
        _arguments = arguments ?? [];
        _number = number;
    }

    public XPathValueKind Kind { get; }

    public string Text => _text;

    public string? AttributeName => Kind == XPathValueKind.Attribute ? _text : null;

    public string? FunctionName => Kind == XPathValueKind.Function ? _text : null;

    public IReadOnlyList<XPathValue> Arguments => _arguments;

    public static XPathValue Number(double value) => new(XPathValueKind.Number, string.Empty, number: value);

    public double AsNumber() => Kind == XPathValueKind.Number ? _number : 0;

    public string Evaluate(DomNode context)
    {
        switch (Kind)
        {
            case XPathValueKind.String:
                return _text;

            case XPathValueKind.Attribute:
                return context.GetAttribute(_text) ?? string.Empty;

            case XPathValueKind.TextContent:
                return context.TextContent();

            case XPathValueKind.Number:
                return _number.ToString(CultureInfo.InvariantCulture);

            case XPathValueKind.Function:
                return EvaluateFunction(context);

            default:
                return string.Empty;
        }
    }

    public bool AsBoolean(DomNode context)
    {
        switch (Kind)
        {
            case XPathValueKind.Attribute:
                return context.GetAttribute(_text) is { } value && value.Length > 0;

            case XPathValueKind.Function when _text is "last" or "position" or "count":
                return AsNumber() != 0;

            case XPathValueKind.String:
                return _text.Length > 0;

            default:
                return Evaluate(context).Length > 0;
        }
    }

    private string EvaluateFunction(DomNode context)
    {
        switch (_text)
        {
            case "text":
            case "string":
                return context.TextContent();

            case "normalize-space":
                return NormalizeSpace(_arguments.Length > 0 ? _arguments[0].Evaluate(context) : context.TextContent());

            case "contains":
                return (_arguments.Length > 1
                    && _arguments[0].Evaluate(context).Contains(_arguments[1].Evaluate(context), StringComparison.Ordinal))
                    ? "true"
                    : "false";

            case "starts-with":
                return (_arguments.Length > 1
                    && _arguments[0].Evaluate(context).StartsWith(_arguments[1].Evaluate(context), StringComparison.Ordinal))
                    ? "true"
                    : "false";

            default:
                return string.Empty;
        }
    }

    private static string NormalizeSpace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static bool EvaluateBoolean(XPathValue value, DomNode context)
    {
        if (value.Kind == XPathValueKind.Function && value.FunctionName is "contains" or "starts-with")
        {
            return value.Evaluate(context) == "true";
        }

        return value.AsBoolean(context);
    }
}

internal sealed class XPathValuePredicate : XPathPredicate
{
    private readonly XPathValue _value;

    public XPathValuePredicate(XPathValue value) => _value = value;

    public override bool Matches(DomNode context, int position, int size)
    {
        if (_value.Kind == XPathValueKind.Number)
        {
            return position == (int)_value.AsNumber();
        }

        if (_value.Kind == XPathValueKind.Function && _value.FunctionName == "last")
        {
            return position == size;
        }

        if (_value.Kind == XPathValueKind.Function && _value.FunctionName == "position")
        {
            return position == (int)PositionValue(_value, position);
        }

        return XPathValue.EvaluateBoolean(_value, context);
    }

    private static double PositionValue(XPathValue value, int position)
        => value.FunctionName == "position" ? position : 0;
}

internal sealed class XPathComparisonPredicate : XPathPredicate
{
    private readonly XPathValue _left;
    private readonly XPathValue _right;
    private readonly bool _negate;

    public XPathComparisonPredicate(XPathValue left, XPathValue right, string op)
    {
        _left = left;
        _right = right;
        _negate = op == "!=";
    }

    public override bool Matches(DomNode context, int position, int size)
    {
        string left = Resolve(_left, context, position);
        string right = Resolve(_right, context, position);
        bool equal = left.Equals(right, StringComparison.Ordinal);
        return _negate ? !equal : equal;
    }

    private static string Resolve(XPathValue value, DomNode context, int position)
        => value.Kind == XPathValueKind.Function && value.FunctionName == "position"
            ? position.ToString(CultureInfo.InvariantCulture)
            : value.Evaluate(context);
}

internal sealed class XPathLogicalPredicate : XPathPredicate
{
    private readonly XPathPredicate _left;
    private readonly XPathPredicate _right;
    private readonly bool _isOr;

    public XPathLogicalPredicate(XPathPredicate left, XPathPredicate right, bool isOr)
    {
        _left = left;
        _right = right;
        _isOr = isOr;
    }

    public override bool Matches(DomNode context, int position, int size)
        => _isOr
            ? _left.Matches(context, position, size) || _right.Matches(context, position, size)
            : _left.Matches(context, position, size) && _right.Matches(context, position, size);
}

internal sealed class XPathNotPredicate : XPathPredicate
{
    private readonly XPathPredicate _inner;

    public XPathNotPredicate(XPathPredicate inner) => _inner = inner;

    public override bool Matches(DomNode context, int position, int size)
        => !_inner.Matches(context, position, size);
}
