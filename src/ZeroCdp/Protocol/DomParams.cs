using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>DOM.querySelectorAll</c>.</summary>
public readonly struct QuerySelectorAllParams : ICdpCommandParams
{
    private readonly long _nodeId;
    private readonly string _selector;

    public QuerySelectorAllParams(long nodeId, string selector)
    {
        _nodeId = nodeId;
        _selector = selector;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
        writer.Name("selector"u8);
        writer.StringValue(_selector);
    }
}

/// <summary>Result of <c>DOM.querySelectorAll</c>.</summary>
public readonly struct NodeIdsResult : ICdpResult<NodeIdsResult>
{
    public long[] NodeIds { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out NodeIdsResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("nodeIds"u8) || !cursor.TryEnterArray())
        {
            return false;
        }

        List<long> ids = new(4);
        while (cursor.TryNextArrayElement())
        {
            if (cursor.TryGetInt64(out long id))
            {
                ids.Add(id);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new NodeIdsResult { NodeIds = ids.ToArray() };
        return true;
    }
}

/// <summary>Parameters of <c>DOM.pushNodesByBackendIdsToFrontend</c>.</summary>
public readonly struct PushNodesByBackendIdsParams : ICdpCommandParams
{
    private readonly long[] _backendNodeIds;

    public PushNodesByBackendIdsParams(long[] backendNodeIds) => _backendNodeIds = backendNodeIds;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("backendNodeIds"u8);
        writer.Value("["u8);
        for (int i = 0; i < _backendNodeIds.Length; i++)
        {
            if (i > 0)
            {
                writer.Value(","u8);
            }

            writer.Int64Value(_backendNodeIds[i]);
        }

        writer.Value("]"u8);
    }
}

/// <summary>Parameters of <c>DOM.describeNode</c>.</summary>
public readonly struct DescribeNodeParams : ICdpCommandParams
{
    private readonly long _nodeId;
    private readonly int _depth;
    private readonly bool _pierce;

    public DescribeNodeParams(long nodeId, int depth, bool pierce)
    {
        _nodeId = nodeId;
        _depth = depth;
        _pierce = pierce;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
        writer.Name("depth"u8);
        writer.Int32Value(_depth);
        writer.Name("pierce"u8);
        writer.BoolValue(_pierce);
    }
}

/// <summary>Result of <c>DOM.describeNode</c>, keeping the raw node JSON for the tree parser.</summary>
public readonly struct NodeDescription : ICdpResult<NodeDescription>
{
    public ReadOnlyMemory<byte> NodeJson { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out NodeDescription value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("node"u8))
        {
            return false;
        }

        int start = cursor.Position;
        if (!cursor.SkipValue())
        {
            return false;
        }

        value = new NodeDescription { NodeJson = resultUtf8[start..cursor.Position].ToArray() };
        return true;
    }
}

/// <summary>Result of <c>DOM.getDocument</c> with the whole tree kept as raw JSON.</summary>
public readonly struct DocumentTree : ICdpResult<DocumentTree>
{
    public ReadOnlyMemory<byte> RootJson { get; init; }

    public long RootNodeId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out DocumentTree value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("root"u8))
        {
            return false;
        }

        int start = cursor.Position;
        if (!cursor.SkipValue())
        {
            return false;
        }

        ReadOnlySpan<byte> root = resultUtf8[start..cursor.Position];

        long nodeId = 0;
        JsonCursor probe = new(root);
        if (probe.TryEnterObject() && probe.TrySeekProperty("nodeId"u8))
        {
            probe.TryGetInt64(out nodeId);
        }

        value = new DocumentTree { RootJson = root.ToArray(), RootNodeId = nodeId };
        return true;
    }
}

/// <summary>Parameters of <c>DOM.getContentQuads</c>.</summary>
public readonly struct GetContentQuadsParams : ICdpCommandParams
{
    private readonly long _nodeId;

    public GetContentQuadsParams(long nodeId) => _nodeId = nodeId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
    }
}

/// <summary>Result of <c>DOM.getContentQuads</c>: one 8-number quad per visible box.</summary>
public readonly struct ContentQuads : ICdpResult<ContentQuads>
{
    public double[][] Quads { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out ContentQuads value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("quads"u8) || !cursor.TryEnterArray())
        {
            return false;
        }

        List<double[]> quads = new(1);
        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterArray())
            {
                return false;
            }

            List<double> points = new(8);
            while (cursor.TryNextArrayElement())
            {
                if (cursor.TryGetDouble(out double coordinate))
                {
                    points.Add(coordinate);
                }
            }

            if (points.Count >= 8)
            {
                quads.Add(points.ToArray());
            }
        }

        value = new ContentQuads { Quads = quads.ToArray() };
        return true;
    }
}

/// <summary>Parameters of <c>DOM.scrollIntoViewIfNeeded</c>.</summary>
public readonly struct ScrollIntoViewParams : ICdpCommandParams
{
    private readonly long _nodeId;

    public ScrollIntoViewParams(long nodeId) => _nodeId = nodeId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
    }
}

/// <summary>Parameters of <c>DOM.focus</c>.</summary>
public readonly struct FocusNodeParams : ICdpCommandParams
{
    private readonly long _nodeId;

    public FocusNodeParams(long nodeId) => _nodeId = nodeId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
    }
}

/// <summary>Parameters of <c>DOM.requestNode</c>.</summary>
public readonly struct RequestNodeParams : ICdpCommandParams
{
    private readonly string _objectId;

    public RequestNodeParams(string objectId) => _objectId = objectId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("objectId"u8);
        writer.StringValue(_objectId);
    }
}

/// <summary>Result of <c>DOM.requestNode</c>.</summary>
public readonly struct RequestNodeResult : ICdpResult<RequestNodeResult>
{
    public long NodeId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out RequestNodeResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("nodeId"u8))
        {
            return false;
        }

        if (!cursor.TryGetInt64(out long nodeId))
        {
            return false;
        }

        value = new RequestNodeResult { NodeId = nodeId };
        return true;
    }
}

/// <summary>Parameters of <c>DOM.resolveNode</c>.</summary>
public readonly struct ResolveNodeParams : ICdpCommandParams
{
    private readonly long _nodeId;
    private readonly long _backendNodeId;
    private readonly int _executionContextId;
    private readonly bool _useBackendNodeId;

    public ResolveNodeParams(long nodeId, int executionContextId = 0)
    {
        _nodeId = nodeId;
        _backendNodeId = 0;
        _executionContextId = executionContextId;
        _useBackendNodeId = false;
    }

    public ResolveNodeParams(long backendNodeId, bool useBackendNodeId, int executionContextId = 0)
    {
        _nodeId = 0;
        _backendNodeId = backendNodeId;
        _executionContextId = executionContextId;
        _useBackendNodeId = useBackendNodeId;
    }

    public void Write(ref CommandWriter writer)
    {
        if (_useBackendNodeId)
        {
            writer.Name("backendNodeId"u8);
            writer.Int64Value(_backendNodeId);
        }
        else
        {
            writer.Name("nodeId"u8);
            writer.Int64Value(_nodeId);
        }

        if (_executionContextId > 0)
        {
            writer.Name("executionContextId"u8);
            writer.Int32Value(_executionContextId);
        }
    }
}

/// <summary>Result of <c>DOM.resolveNode</c>.</summary>
public readonly struct ResolveNodeResult : ICdpResult<ResolveNodeResult>
{
    public string? ObjectId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out ResolveNodeResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("object"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("objectId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                value = new ResolveNodeResult { ObjectId = Encoding.UTF8.GetString(raw) };
                return true;
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Parameters of <c>DOM.getFrameOwner</c>.</summary>
public readonly struct GetFrameOwnerParams : ICdpCommandParams
{
    private readonly string _frameId;

    public GetFrameOwnerParams(string frameId) => _frameId = frameId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("frameId"u8);
        writer.StringValue(_frameId);
    }
}

/// <summary>Result of <c>DOM.getFrameOwner</c>: the iframe element that owns a frame.</summary>
public readonly struct FrameOwnerResult : ICdpResult<FrameOwnerResult>
{
    public long BackendNodeId { get; init; }

    public long NodeId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out FrameOwnerResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        long backendNodeId = 0;
        long nodeId = 0;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("backendNodeId"u8))
            {
                cursor.TryGetInt64(out backendNodeId);
            }
            else if (name.SequenceEqual("nodeId"u8))
            {
                cursor.TryGetInt64(out nodeId);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (backendNodeId == 0)
        {
            return false;
        }

        value = new FrameOwnerResult { BackendNodeId = backendNodeId, NodeId = nodeId };
        return true;
    }
}

/// <summary>Parameters of <c>DOM.getBoxModel</c>.</summary>
public readonly struct GetBoxModelParams : ICdpCommandParams
{
    private readonly long _nodeId;
    private readonly long _backendNodeId;
    private readonly bool _useBackendNodeId;

    public GetBoxModelParams(long nodeId)
    {
        _nodeId = nodeId;
        _backendNodeId = 0;
        _useBackendNodeId = false;
    }

    public GetBoxModelParams(long backendNodeId, bool useBackendNodeId)
    {
        _nodeId = 0;
        _backendNodeId = backendNodeId;
        _useBackendNodeId = useBackendNodeId;
    }

    public void Write(ref CommandWriter writer)
    {
        if (_useBackendNodeId)
        {
            writer.Name("backendNodeId"u8);
            writer.Int64Value(_backendNodeId);
        }
        else
        {
            writer.Name("nodeId"u8);
            writer.Int64Value(_nodeId);
        }
    }
}

/// <summary>
/// Result of <c>DOM.getBoxModel</c>. <see cref="Content"/> is the content quad in the parent
/// viewport's CSS pixels: x1,y1 (top-left), x2,y2 (top-right), x3,y3, x4,y4.
/// </summary>
public readonly struct BoxModelResult : ICdpResult<BoxModelResult>
{
    public double[] Content { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out BoxModelResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("model"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("content"u8) && cursor.TryEnterArray())
            {
                List<double> points = new(8);
                while (cursor.TryNextArrayElement())
                {
                    if (!cursor.TryGetDouble(out double coordinate))
                    {
                        return false;
                    }

                    points.Add(coordinate);
                }

                if (points.Count < 8)
                {
                    return false;
                }

                value = new BoxModelResult { Content = points.ToArray() };
                return true;
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Parameters of <c>Input.insertText</c>.</summary>
public readonly struct InsertTextParams : ICdpCommandParams
{
    private readonly string _text;

    public InsertTextParams(string text) => _text = text;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("text"u8);
        writer.StringValue(_text);
    }
}

/// <summary>Parameters of <c>Input.dispatchMouseEvent</c>.</summary>
public readonly struct DispatchMouseEventParams : ICdpCommandParams
{
    private readonly string _type;
    private readonly double _x;
    private readonly double _y;
    private readonly string? _button;
    private readonly int _buttons;
    private readonly int _clickCount;
    private readonly double _deltaX;
    private readonly double _deltaY;
    private readonly int _modifiers;

    private DispatchMouseEventParams(
        string type,
        double x,
        double y,
        string? button,
        int buttons,
        int clickCount,
        double deltaX,
        double deltaY,
        int modifiers = 0)
    {
        _type = type;
        _x = x;
        _y = y;
        _button = button;
        _buttons = buttons;
        _clickCount = clickCount;
        _deltaX = deltaX;
        _deltaY = deltaY;
        _modifiers = modifiers;
    }

    public static DispatchMouseEventParams Moved(double x, double y, int modifiers = 0, int buttons = 0)
        => new("mouseMoved", x, y, button: null, buttons, clickCount: 0, 0, 0, modifiers);

    public static DispatchMouseEventParams Dragged(double x, double y, int modifiers = 0)
        => new("mouseMoved", x, y, "left", 1, clickCount: 0, 0, 0, modifiers);

    public static DispatchMouseEventParams Pressed(
        double x,
        double y,
        string button = "left",
        int clickCount = 1,
        int modifiers = 0)
        => new("mousePressed", x, y, button, ButtonMask(button), clickCount, 0, 0, modifiers);

    public static DispatchMouseEventParams Released(
        double x,
        double y,
        string button = "left",
        int clickCount = 1,
        int modifiers = 0)
        => new("mouseReleased", x, y, button, buttons: 0, clickCount, 0, 0, modifiers);

    public static DispatchMouseEventParams Wheel(double x, double y, double deltaX, double deltaY, int modifiers = 0)
        => new("mouseWheel", x, y, button: null, buttons: 0, clickCount: 0, deltaX, deltaY, modifiers);

    private static int ButtonMask(string button) => button switch
    {
        "left" => 1,
        "right" => 2,
        "middle" => 4,
        _ => 0,
    };

    public void Write(ref CommandWriter writer)
    {
        writer.Name("type"u8);
        writer.StringValue(_type);
        writer.Name("x"u8);
        writer.DoubleValue(_x);
        writer.Name("y"u8);
        writer.DoubleValue(_y);

        if (_button is not null)
        {
            writer.Name("button"u8);
            writer.StringValue(_button);
            writer.Name("buttons"u8);
            writer.Int32Value(_buttons);
            writer.Name("clickCount"u8);
            writer.Int32Value(_clickCount);
        }
        else if (_buttons != 0)
        {
            // A move with a held button is what turns a press into an HTML5 drag.
            writer.Name("buttons"u8);
            writer.Int32Value(_buttons);
        }

        if (_deltaX != 0 || _deltaY != 0)
        {
            writer.Name("deltaX"u8);
            writer.DoubleValue(_deltaX);
            writer.Name("deltaY"u8);
            writer.DoubleValue(_deltaY);
        }

        if (_modifiers != 0)
        {
            writer.Name("modifiers"u8);
            writer.Int32Value(_modifiers);
        }
    }
}

/// <summary>Parameters of <c>Input.dispatchKeyEvent</c>.</summary>
public readonly struct DispatchKeyEventParams : ICdpCommandParams
{
    private readonly string _type;
    private readonly string _key;
    private readonly string? _text;
    private readonly string? _code;
    private readonly int _virtualKeyCode;
    private readonly int _modifiers;

    public DispatchKeyEventParams(
        string type,
        string key,
        string? text,
        int virtualKeyCode,
        string? code = null,
        int modifiers = 0)
    {
        _type = type;
        _key = key;
        _text = text;
        _code = code;
        _virtualKeyCode = virtualKeyCode;
        _modifiers = modifiers;
    }

    public static DispatchKeyEventParams KeyDown(string text)
        => new("keyDown", text, text, 0);

    public static DispatchKeyEventParams KeyUp(string key)
        => new("keyUp", key, null, 0);

    public void Write(ref CommandWriter writer)
    {
        writer.Name("type"u8);
        writer.StringValue(_type);
        writer.Name("key"u8);
        writer.StringValue(_key);

        if (_code is not null)
        {
            writer.Name("code"u8);
            writer.StringValue(_code);
        }

        if (_text is not null)
        {
            writer.Name("text"u8);
            writer.StringValue(_text);
        }

        if (_virtualKeyCode != 0)
        {
            writer.Name("windowsVirtualKeyCode"u8);
            writer.Int32Value(_virtualKeyCode);
            writer.Name("nativeVirtualKeyCode"u8);
            writer.Int32Value(_virtualKeyCode);
        }

        if (_modifiers != 0)
        {
            writer.Name("modifiers"u8);
            writer.Int32Value(_modifiers);
        }
    }
}

/// <summary>Parameters of <c>DOM.setFileInputFiles</c>.</summary>
public readonly struct SetFileInputFilesParams : ICdpCommandParams
{
    private readonly long _nodeId;
    private readonly long _backendNodeId;
    private readonly IReadOnlyList<string> _files;
    private readonly bool _useBackendNodeId;

    public SetFileInputFilesParams(long nodeId, IReadOnlyList<string> files)
    {
        _nodeId = nodeId;
        _backendNodeId = 0;
        _files = files;
        _useBackendNodeId = false;
    }

    public SetFileInputFilesParams(IReadOnlyList<string> files, long backendNodeId)
    {
        _nodeId = 0;
        _backendNodeId = backendNodeId;
        _files = files;
        _useBackendNodeId = true;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("files"u8);
        writer.StringArray(_files);
        if (_useBackendNodeId)
        {
            writer.Name("backendNodeId"u8);
            writer.Int64Value(_backendNodeId);
        }
        else
        {
            writer.Name("nodeId"u8);
            writer.Int64Value(_nodeId);
        }
    }
}

/// <summary>Parameters of <c>Input.dispatchTouchEvent</c>.</summary>
public readonly struct DispatchTouchEventParams : ICdpCommandParams
{
    private readonly string _type;
    private readonly double _x;
    private readonly double _y;
    private readonly int _modifiers;

    public DispatchTouchEventParams(string type, double x, double y, int modifiers = 0)
    {
        _type = type;
        _x = x;
        _y = y;
        _modifiers = modifiers;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("type"u8);
        writer.StringValue(_type);
        writer.Name("touchPoints"u8);
        writer.Value("["u8);
        writer.BeginObject();
        writer.Name("x"u8);
        writer.DoubleValue(_x);
        writer.Name("y"u8);
        writer.DoubleValue(_y);
        writer.EndObject();
        writer.Value("]"u8);

        if (_modifiers != 0)
        {
            writer.Name("modifiers"u8);
            writer.Int32Value(_modifiers);
        }
    }
}
