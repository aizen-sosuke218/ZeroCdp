using ZeroCdp.Json;

namespace ZeroCdp.Dom;

/// <summary>Builds a <see cref="DomNode"/> tree from a CDP <c>Node</c> JSON object.</summary>
/// <remarks>
/// Parsing is recursive but depth-capped: a page that nests thousands of elements must not be
/// able to overflow our stack. Locators that need more depth can still use
/// <c>DOM.querySelectorAll</c>, which the browser evaluates.
/// </remarks>
internal static class DomTreeParser
{
    /// <summary>Deepest level of markup we are willing to materialise.</summary>
    public const int MaxDepth = 256;

    public static DomNode? Parse(ReadOnlySpan<byte> nodeJson)
    {
        JsonCursor cursor = new(nodeJson);
        return ParseNode(ref cursor, depth: 0, parent: null);
    }

    private static DomNode? ParseNode(ref JsonCursor cursor, int depth, DomNode? parent)
    {
        if (!cursor.TryEnterObject())
        {
            return null;
        }

        DomNode node = new() { Parent = parent };
        bool truncated = depth >= MaxDepth;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("nodeType"u8))
            {
                cursor.TryGetInt32(out int nodeType);
                node.NodeType = nodeType;
            }
            else if (name.SequenceEqual("nodeName"u8))
            {
                node.NodeName = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("localName"u8))
            {
                node.LocalName = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("nodeValue"u8))
            {
                node.NodeValue = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("nodeId"u8))
            {
                cursor.TryGetInt64(out long nodeId);
                node.NodeId = nodeId;
            }
            else if (name.SequenceEqual("backendNodeId"u8))
            {
                cursor.TryGetInt64(out long backendNodeId);
                node.BackendNodeId = backendNodeId;
            }
            else if (name.SequenceEqual("frameId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> frameId, out bool escaped))
                {
                    node.FrameId = escaped
                        ? System.Text.Encoding.UTF8.GetString(Unescape(frameId))
                        : System.Text.Encoding.UTF8.GetString(frameId);
                }
            }
            else if (name.SequenceEqual("attributes"u8))
            {
                node.Attributes = ReadAttributes(ref cursor);
            }
            else if (name.SequenceEqual("children"u8))
            {
                ReadNodeList(ref cursor, node, depth, truncated, intoShadowRoots: false);
            }
            else if (name.SequenceEqual("shadowRoots"u8))
            {
                ReadNodeList(ref cursor, node, depth, truncated, intoShadowRoots: true);
            }
            else if (name.SequenceEqual("contentDocument"u8))
            {
                // An iframe's document. Treated as a child so descendant selectors can reach it.
                if (!truncated)
                {
                    DomNode? contentDocument = ParseNode(ref cursor, depth + 1, node);
                    if (contentDocument is not null)
                    {
                        (node.Children ??= []).Add(contentDocument);
                    }
                }
                else if (!cursor.SkipValue())
                {
                    return null;
                }
            }
            else if (!cursor.SkipValue())
            {
                return null;
            }
        }

        return node;
    }

    private static void ReadNodeList(
        ref JsonCursor cursor,
        DomNode parent,
        int depth,
        bool truncated,
        bool intoShadowRoots)
    {
        if (!cursor.TryEnterArray())
        {
            return;
        }

        while (cursor.TryNextArrayElement())
        {
            if (truncated)
            {
                if (!cursor.SkipValue())
                {
                    return;
                }

                continue;
            }

            DomNode? child = ParseNode(ref cursor, depth + 1, parent);
            if (child is null)
            {
                return;
            }

            if (intoShadowRoots)
            {
                (parent.ShadowRoots ??= []).Add(child);
            }
            else
            {
                (parent.Children ??= []).Add(child);
            }
        }
    }

    private static string[] ReadAttributes(ref JsonCursor cursor)
    {
        if (!cursor.TryEnterArray())
        {
            return [];
        }

        List<string> values = new(8);
        while (cursor.TryNextArrayElement())
        {
            values.Add(ReadString(ref cursor));
        }

        return values.ToArray();
    }

    private static string ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return string.Empty;
        }

        if (!escaped)
        {
            return System.Text.Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }

    private static byte[] Unescape(ReadOnlySpan<byte> raw)
    {
        byte[] buffer = new byte[raw.Length];
        if (!JsonUtf8.TryUnescape(raw, buffer, out int written))
        {
            return [];
        }

        return written == buffer.Length ? buffer : buffer[..written];
    }
}
