using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="Accessibility.SnapshotAsync"/>.</summary>
public sealed class AccessibilitySnapshotOptions
{
    /// <summary>
    /// Drop ignored and presentational nodes, promoting their children. Defaults to true, which
    /// mirrors Playwright's accessibility snapshot.
    /// </summary>
    public bool InterestingOnly { get; init; } = true;
}

/// <summary>One node in a Chromium accessibility tree.</summary>
public sealed class AccessibilityNode
{
    public string Role { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Value { get; init; }

    public string? Description { get; init; }

    public bool IsIgnored { get; init; }

    /// <summary>AX properties such as <c>level</c>, <c>checked</c> and <c>expanded</c>.</summary>
    public IReadOnlyList<AccessibilityProperty> Properties { get; init; } = Array.Empty<AccessibilityProperty>();

    public IReadOnlyList<AccessibilityNode> Children { get; init; } = Array.Empty<AccessibilityNode>();

    /// <summary>
    /// Serializes this subtree in the YAML shape used by Playwright's <c>ariaSnapshot</c>.
    /// The format is intentionally compact; it is not a full YAML document.
    /// </summary>
    public string ToYaml()
    {
        StringBuilder builder = new(128);
        AppendYaml(builder, indent: 0);
        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>Finds a descendant by role and, optionally, accessible name.</summary>
    public AccessibilityNode? Find(string role, string? name = null)
    {
        if (Role.Equals(role, StringComparison.OrdinalIgnoreCase)
            && (name is null || Name.Equals(name, StringComparison.Ordinal)))
        {
            return this;
        }

        foreach (AccessibilityNode child in Children)
        {
            AccessibilityNode? found = child.Find(role, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void AppendYaml(StringBuilder builder, int indent)
    {
        if (IsFlattenable)
        {
            foreach (AccessibilityNode child in Children)
            {
                child.AppendYaml(builder, indent);
            }

            return;
        }

        builder.Append(' ', indent);
        builder.Append("- ");
        builder.Append(Role);

        if (Name.Length > 0)
        {
            builder.Append(' ');
            AppendQuoted(builder, Name);
        }

        if (Properties.Count > 0)
        {
            bool first = true;
            foreach (AccessibilityProperty property in Properties)
            {
                if (!IsSnapshotProperty(property))
                {
                    continue;
                }

                if (first)
                {
                    builder.Append(" [");
                    first = false;
                }
                else
                {
                    builder.Append(", ");
                }

                builder.Append(property.Name);
                if (property.Value == "true")
                {
                    continue;
                }

                builder.Append('=');
                if (NeedsQuoting(property.Value))
                {
                    AppendQuoted(builder, property.Value);
                }
                else
                {
                    builder.Append(property.Value);
                }
            }

            if (!first)
            {
                builder.Append(']');
            }
        }

        builder.Append('\n');

        foreach (AccessibilityNode child in Children)
        {
            if (ShouldSkipTextChild(child))
            {
                continue;
            }

            child.AppendYaml(builder, indent + 2);
        }
    }

    private bool IsFlattenable
        => Name.Length == 0
            && !HasSnapshotProperties
            && (Role.Equals("generic", StringComparison.OrdinalIgnoreCase)
                || Role.Equals("none", StringComparison.OrdinalIgnoreCase)
                || Role.Equals("presentation", StringComparison.OrdinalIgnoreCase));

    private bool HasSnapshotProperties
    {
        get
        {
            foreach (AccessibilityProperty property in Properties)
            {
                if (IsSnapshotProperty(property))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool ShouldSkipTextChild(AccessibilityNode child)
        => Name.Length > 0
            && (child.Role.Equals("StaticText", StringComparison.OrdinalIgnoreCase)
                || child.Role.Equals("InlineTextBox", StringComparison.OrdinalIgnoreCase)
                || child.Role.Equals("text", StringComparison.OrdinalIgnoreCase));

    private static bool IsSnapshotProperty(AccessibilityProperty property)
    {
        if (property.Value == "false")
        {
            return false;
        }

        return property.Name switch
        {
            "level" or "checked" or "disabled" or "expanded" or "pressed" or "selected" => true,
            _ => false,
        };
    }

    private static bool NeedsQuoting(string value)
    {
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c is ',' or '[' or ']' or '"' or '\\')
            {
                return true;
            }
        }

        return value.Length == 0;
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }
}

/// <summary>One accessibility property exposed by Chromium.</summary>
public sealed class AccessibilityProperty
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// Accessibility-domain access for a page. The snapshot is built from Chromium's AX tree, so
/// roles, names and properties use the browser's computed values rather than a JavaScript
/// approximation.
/// </summary>
public sealed class Accessibility
{
    private readonly CdpPage _page;

    internal Accessibility(CdpPage page) => _page = page;

    /// <summary>Returns the current full accessibility tree for the page.</summary>
    public async ValueTask<AccessibilityNode?> SnapshotAsync(
        AccessibilitySnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_page.IsClosed, _page);
        options ??= new AccessibilitySnapshotOptions();

        AXTreeResult tree = await _page.Session
            .SendAsync<AXTreeResult>("Accessibility.getFullAXTree"u8, cancellationToken)
            .ConfigureAwait(false);

        return AccessibilityTreeParser.Parse(tree.NodesJson, options.InterestingOnly);
    }
}

internal static class AccessibilityTreeParser
{
    public static AccessibilityNode? Parse(
        ReadOnlyMemory<byte> nodesJson,
        bool interestingOnly,
        long? backendNodeId = null)
    {
        using JsonDocument document = JsonDocument.Parse(nodesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<AxNodeData> nodes = new();
        Dictionary<string, AxNodeData> byId = new(StringComparer.Ordinal);
        AxNodeData? target = null;
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            AxNodeData? node = ReadNode(element);
            if (node is null)
            {
                continue;
            }

            nodes.Add(node);
            byId[node.NodeId] = node;
            if (backendNodeId is { } wanted && node.BackendNodeId == wanted)
            {
                target = node;
            }
        }

        if (target is not null)
        {
            return CreateNode(
                target,
                byId,
                new HashSet<string>(StringComparer.Ordinal),
                interestingOnly);
        }

        if (backendNodeId is not null)
        {
            return null;
        }

        HashSet<string> childIds = new(StringComparer.Ordinal);
        foreach (AxNodeData node in nodes)
        {
            foreach (string childId in node.ChildIds)
            {
                childIds.Add(childId);
            }
        }

        List<AccessibilityNode> roots = new();
        HashSet<string> visiting = new(StringComparer.Ordinal);
        foreach (AxNodeData node in nodes)
        {
            if (!childIds.Contains(node.NodeId))
            {
                Collect(node, byId, visiting, interestingOnly, roots);
            }
        }

        return roots.Count > 0 ? roots[0] : null;
    }

    private static void Collect(
        AxNodeData data,
        Dictionary<string, AxNodeData> byId,
        HashSet<string> visiting,
        bool interestingOnly,
        List<AccessibilityNode> target)
    {
        AccessibilityNode? node = CreateNode(data, byId, visiting, interestingOnly);
        if (node is null)
        {
            return;
        }

        if (interestingOnly && data.Ignored)
        {
            target.AddRange(node.Children);
            return;
        }

        target.Add(node);
    }

    private static AccessibilityNode? CreateNode(
        AxNodeData data,
        Dictionary<string, AxNodeData> byId,
        HashSet<string> visiting,
        bool interestingOnly)
    {
        if (!visiting.Add(data.NodeId))
        {
            return null;
        }

        List<AccessibilityNode> children = new();
        foreach (string childId in data.ChildIds)
        {
            if (byId.TryGetValue(childId, out AxNodeData? child))
            {
                Collect(child, byId, visiting, interestingOnly, children);
            }
        }

        visiting.Remove(data.NodeId);
        return new AccessibilityNode
        {
            Role = data.Role,
            Name = data.Name,
            Value = data.Value,
            Description = data.Description,
            IsIgnored = data.Ignored,
            Properties = data.Properties,
            Children = children,
        };
    }

    private static AxNodeData? ReadNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string nodeId = ReadString(element, "nodeId");
        if (nodeId.Length == 0)
        {
            return null;
        }

        bool ignored = element.TryGetProperty("ignored", out JsonElement ignoredElement)
            && ignoredElement.ValueKind == JsonValueKind.True;

        List<string> childIds = new();
        if (element.TryGetProperty("childIds", out JsonElement childrenElement)
            && childrenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in childrenElement.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String)
                {
                    childIds.Add(child.GetString() ?? string.Empty);
                }
            }
        }

        List<AccessibilityProperty> properties = new();
        if (element.TryGetProperty("properties", out JsonElement propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement property in propertiesElement.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = ReadString(property, "name");
                if (name.Length == 0
                    || !property.TryGetProperty("value", out JsonElement valueElement)
                    || valueElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string value = ReadValue(valueElement);
                if (value.Length > 0)
                {
                    properties.Add(new AccessibilityProperty { Name = name, Value = value });
                }
            }
        }

        return new AxNodeData
        {
            NodeId = nodeId,
            BackendNodeId = ReadInt64(element, "backendDOMNodeId"),
            Role = ReadAXValue(element, "role"),
            Name = ReadAXValue(element, "name"),
            Value = EmptyToNull(ReadAXValue(element, "value")),
            Description = EmptyToNull(ReadAXValue(element, "description")),
            Ignored = ignored,
            Properties = properties,
            ChildIds = childIds,
        };
    }

    private static string ReadAXValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadValue(value);
    }

    private static string ReadValue(JsonElement value)
    {
        if (!value.TryGetProperty("value", out JsonElement scalar))
        {
            return string.Empty;
        }

        return scalar.ValueKind switch
        {
            JsonValueKind.String => scalar.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number when scalar.TryGetInt64(out long integer)
                => integer.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => scalar.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long integer)
            ? integer
            : 0;

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private sealed class AxNodeData
    {
        public string NodeId { get; init; } = string.Empty;

        public long BackendNodeId { get; init; }

        public string Role { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? Value { get; init; }

        public string? Description { get; init; }

        public bool Ignored { get; init; }

        public IReadOnlyList<AccessibilityProperty> Properties { get; init; } = Array.Empty<AccessibilityProperty>();

        public IReadOnlyList<string> ChildIds { get; init; } = Array.Empty<string>();
    }
}
