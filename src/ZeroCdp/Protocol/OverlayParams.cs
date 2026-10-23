namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Overlay.highlightNode</c>.</summary>
public readonly struct HighlightNodeParams : ICdpCommandParams
{
    private readonly long _nodeId;

    public HighlightNodeParams(long nodeId) => _nodeId = nodeId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
        writer.Name("highlightConfig"u8);
        writer.BeginObject();
        writer.Name("showInfo"u8);
        writer.BoolValue(true);
        writer.Name("contentColor"u8);
        WriteColor(ref writer, 111, 168, 220, 0.5);
        writer.Name("paddingColor"u8);
        WriteColor(ref writer, 147, 196, 125, 0.55);
        writer.Name("borderColor"u8);
        WriteColor(ref writer, 255, 229, 153, 0.8);
        writer.Name("marginColor"u8);
        WriteColor(ref writer, 246, 178, 107, 0.6);
        writer.EndObject();
    }

    private static void WriteColor(ref CommandWriter writer, int r, int g, int b, double a)
    {
        writer.BeginObject();
        writer.Name("r"u8);
        writer.Int32Value(r);
        writer.Name("g"u8);
        writer.Int32Value(g);
        writer.Name("b"u8);
        writer.Int32Value(b);
        writer.Name("a"u8);
        writer.DoubleValue(a);
        writer.EndObject();
    }
}
