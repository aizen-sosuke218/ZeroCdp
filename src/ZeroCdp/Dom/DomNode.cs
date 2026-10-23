using System.Text;

namespace ZeroCdp.Dom;

/// <summary>One node of a CDP DOM snapshot.</summary>
internal sealed class DomNode
{
    public const int ElementNode = 1;
    public const int TextNode = 3;
    public const int DocumentNode = 9;
    public const int DocumentFragmentNode = 11;

    public int NodeType { get; set; }

    /// <summary>Upper-case node name for elements (<c>DIV</c>), <c>#text</c> for text.</summary>
    public string NodeName { get; set; } = string.Empty;

    /// <summary>Lower-case tag name for elements.</summary>
    public string LocalName { get; set; } = string.Empty;

    public string? NodeValue { get; set; }

    public long NodeId { get; set; }

    public long BackendNodeId { get; set; }

    /// <summary>Frame id for frame owner elements (<c>iframe</c>, <c>frame</c>), otherwise null.</summary>
    public string? FrameId { get; set; }

    /// <summary>Flat name/value pairs, in document order.</summary>
    public string[] Attributes { get; set; } = [];

    public List<DomNode>? Children { get; set; }

    public List<DomNode>? ShadowRoots { get; set; }

    public DomNode? Parent { get; set; }

    public bool IsElement => NodeType == ElementNode;

    public bool IsDocument => NodeType == DocumentNode;

    public DomNode? ShadowRoot => ShadowRoots is { Count: > 0 } roots ? roots[0] : null;

    public string? GetAttribute(ReadOnlySpan<char> name)
    {
        string[] attributes = Attributes;
        for (int i = 0; i + 1 < attributes.Length; i += 2)
        {
            if (attributes[i].AsSpan().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return attributes[i + 1];
            }
        }

        return null;
    }

    public bool HasAttribute(ReadOnlySpan<char> name) => GetAttribute(name) is not null;

    /// <summary>Element children, ignoring text nodes.</summary>
    public IEnumerable<DomNode> ElementChildren()
    {
        if (Children is null)
        {
            yield break;
        }

        foreach (DomNode child in Children)
        {
            if (child.IsElement)
            {
                yield return child;
            }
        }
    }

    /// <summary>Visible-to-us tree children: element children plus shadow roots of the element.</summary>
    public IEnumerable<DomNode> TraversalChildren()
    {
        if (Children is not null)
        {
            foreach (DomNode child in Children)
            {
                yield return child;
            }
        }

        if (ShadowRoots is not null)
        {
            foreach (DomNode root in ShadowRoots)
            {
                yield return root;
            }
        }
    }

    /// <summary>1-based position among element siblings, or 0 when there is no parent.</summary>
    public int ElementIndex()
    {
        DomNode? parent = Parent;
        if (parent?.Children is null)
        {
            return 1;
        }

        int index = 1;
        foreach (DomNode sibling in parent.Children)
        {
            if (ReferenceEquals(sibling, this))
            {
                return index;
            }

            if (sibling.IsElement)
            {
                index++;
            }
        }

        return 0;
    }

    public int ElementCount()
    {
        DomNode? parent = Parent;
        if (parent?.Children is null)
        {
            return 1;
        }

        int count = 0;
        foreach (DomNode sibling in parent.Children)
        {
            if (sibling.IsElement)
            {
                count++;
            }
        }

        return count;
    }

    public DomNode? PreviousElementSibling()
    {
        DomNode? parent = Parent;
        if (parent?.Children is null)
        {
            return null;
        }

        DomNode? previous = null;
        foreach (DomNode sibling in parent.Children)
        {
            if (ReferenceEquals(sibling, this))
            {
                return previous;
            }

            if (sibling.IsElement)
            {
                previous = sibling;
            }
        }

        return null;
    }

    /// <summary>Concatenated text of this node's subtree, matching <c>textContent</c>.</summary>
    public string TextContent()
    {
        StringBuilder builder = new();
        AppendText(this, builder, depth: 0);
        return builder.ToString();
    }

    private static void AppendText(DomNode node, StringBuilder builder, int depth)
    {
        if (depth > 512)
        {
            return;
        }

        if (node.NodeType == TextNode && node.NodeValue is { Length: > 0 } text)
        {
            builder.Append(text);
            return;
        }

        // <script> and <style> contribute their source to textContent; skip them so element text
        // reads the way a caller expects.
        if (node.IsElement && (node.LocalName is "script" or "style" or "noscript"))
        {
            return;
        }

        if (node.Children is not null)
        {
            foreach (DomNode child in node.Children)
            {
                AppendText(child, builder, depth + 1);
            }
        }

        if (node.ShadowRoots is not null)
        {
            foreach (DomNode root in node.ShadowRoots)
            {
                AppendText(root, builder, depth + 1);
            }
        }
    }
}
