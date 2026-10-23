using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Accessibility.getPartialAXTree</c>.</summary>
public readonly struct GetPartialAXTreeParams : ICdpCommandParams
{
    private readonly long _backendNodeId;
    private readonly bool _fetchRelatives;

    public GetPartialAXTreeParams(long backendNodeId, bool fetchRelatives = false)
    {
        _backendNodeId = backendNodeId;
        _fetchRelatives = fetchRelatives;
    }

    public void Write(ref CommandWriter writer)
    {
        if (_backendNodeId > 0)
        {
            writer.Name("backendNodeId"u8);
            writer.Int64Value(_backendNodeId);
        }

        writer.Name("fetchRelatives"u8);
        writer.BoolValue(_fetchRelatives);
    }
}

/// <summary>Result of <c>Accessibility.getFullAXTree</c> or <c>Accessibility.getPartialAXTree</c>.</summary>
public readonly struct AXTreeResult : ICdpResult<AXTreeResult>
{
    /// <summary>The raw <c>nodes</c> array from the CDP result.</summary>
    public ReadOnlyMemory<byte> NodesJson { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out AXTreeResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject()
            || !cursor.TrySeekProperty("nodes"u8))
        {
            return false;
        }

        int start = cursor.Position;
        if (!cursor.SkipValue())
        {
            return false;
        }

        value = new AXTreeResult { NodesJson = resultUtf8[start..cursor.Position].ToArray() };
        return true;
    }
}
