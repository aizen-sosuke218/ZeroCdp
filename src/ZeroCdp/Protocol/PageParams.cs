using System.Buffers.Text;
using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Page.setDownloadBehavior</c>.</summary>
public readonly struct SetDownloadBehaviorParams : ICdpCommandParams
{
    private readonly string _behavior;
    private readonly string _downloadPath;

    public SetDownloadBehaviorParams(string behavior, string downloadPath)
    {
        _behavior = behavior;
        _downloadPath = downloadPath;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("behavior"u8);
        writer.StringValue(_behavior);
        writer.Name("downloadPath"u8);
        writer.StringValue(_downloadPath);
    }
}

/// <summary>Parameters of <c>Page.setInterceptFileChooserDialog</c>.</summary>
public readonly struct SetInterceptFileChooserDialogParams : ICdpCommandParams
{
    private readonly bool _enabled;

    public SetInterceptFileChooserDialogParams(bool enabled) => _enabled = enabled;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("enabled"u8);
        writer.BoolValue(_enabled);
    }
}

/// <summary>Parameters of <c>Page.setLifecycleEventsEnabled</c>.</summary>
public readonly struct SetLifecycleEventsEnabledParams : ICdpCommandParams
{
    private readonly bool _enabled;

    public SetLifecycleEventsEnabledParams(bool enabled) => _enabled = enabled;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("enabled"u8);
        writer.BoolValue(_enabled);
    }
}

/// <summary>Result of <c>Page.getFrameTree</c>: the main frame id and its current URL.</summary>
public readonly struct FrameTreeResult : ICdpResult<FrameTreeResult>
{
    public string MainFrameId { get; init; }

    public ReadOnlyMemory<byte> MainFrameIdUtf8 { get; init; }

    public string MainFrameUrl { get; init; }

    /// <summary>
    /// Raw <c>Page.getFrameTree</c> result, retained so the browser layer can parse the complete
    /// nested frame tree without adding protocol-specific frame types to this struct.
    /// </summary>
    internal ReadOnlyMemory<byte> RawUtf8 { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out FrameTreeResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("frameTree"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        if (!cursor.TrySeekProperty("frame"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        string id = string.Empty;
        byte[] idUtf8 = [];
        string url = string.Empty;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("id"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawId, out _))
            {
                idUtf8 = rawId.ToArray();
                id = Encoding.UTF8.GetString(rawId);
            }
            else if (name.SequenceEqual("url"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawUrl, out _))
            {
                url = Encoding.UTF8.GetString(rawUrl);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new FrameTreeResult
        {
            MainFrameId = id,
            MainFrameIdUtf8 = idUtf8,
            MainFrameUrl = url,
            RawUtf8 = resultUtf8.ToArray(),
        };
        return id.Length != 0;
    }
}

/// <summary>Parameters of <c>DOM.getDocument</c>.</summary>
public readonly struct GetDocumentParams : ICdpCommandParams
{
    private readonly int _depth;
    private readonly bool _pierce;

    public GetDocumentParams(int depth, bool pierce)
    {
        _depth = depth;
        _pierce = pierce;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("depth"u8);
        writer.Int32Value(_depth);
        writer.Name("pierce"u8);
        writer.BoolValue(_pierce);
    }
}

/// <summary>Result of <c>DOM.getDocument</c>: the root node id.</summary>
public readonly struct DocumentRoot : ICdpResult<DocumentRoot>
{
    public long NodeId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out DocumentRoot value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("root"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        long nodeId = 0;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("nodeId"u8) && cursor.TryGetInt64(out nodeId))
            {
                value = new DocumentRoot { NodeId = nodeId };
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

/// <summary>Parameters of <c>DOM.getOuterHTML</c>.</summary>
public readonly struct GetOuterHtmlParams : ICdpCommandParams
{
    private readonly long _nodeId;

    public GetOuterHtmlParams(long nodeId) => _nodeId = nodeId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("nodeId"u8);
        writer.Int64Value(_nodeId);
    }
}

/// <summary>Result of <c>DOM.getOuterHTML</c>.</summary>
public readonly struct OuterHtml : ICdpResult<OuterHtml>
{
    public string Value { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out OuterHtml value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("outerHTML"u8))
        {
            return false;
        }

        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return false;
        }

        if (!escaped)
        {
            value = new OuterHtml { Value = Encoding.UTF8.GetString(raw) };
            return true;
        }

        byte[] buffer = new byte[raw.Length];
        if (!JsonUtf8.TryUnescape(raw, buffer, out int written))
        {
            return false;
        }

        value = new OuterHtml { Value = Encoding.UTF8.GetString(buffer, 0, written) };
        return true;
    }
}

/// <summary>A rectangular screenshot clip in CSS pixels.</summary>
public readonly struct ScreenshotClip
{
    public ScreenshotClip(double x, double y, double width, double height, double scale = 1)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Scale = scale;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double Scale { get; }
}

/// <summary>Parameters of <c>Page.captureSnapshot</c>.</summary>
public readonly struct CaptureSnapshotParams : ICdpCommandParams
{
    private readonly string _format;

    public CaptureSnapshotParams(string format) => _format = format;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("format"u8);
        writer.StringValue(_format);
    }
}

/// <summary>Result of <c>Page.captureSnapshot</c>.</summary>
public readonly struct CaptureSnapshotResult : ICdpResult<CaptureSnapshotResult>
{
    public string Data { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out CaptureSnapshotResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("data"u8))
        {
            return false;
        }

        string data = CdpHeader.ReadString(ref cursor);
        value = new CaptureSnapshotResult { Data = data };
        return true;
    }
}

/// <summary>Parameters of <c>Page.captureScreenshot</c>.</summary>
public readonly struct CaptureScreenshotParams : ICdpCommandParams
{
    private readonly string _format;
    private readonly int _quality;
    private readonly bool _hasClip;
    private readonly ScreenshotClip _clip;
    private readonly bool _captureBeyondViewport;
    private readonly bool _fromSurface;
    private readonly bool _omitBackground;

    public CaptureScreenshotParams(
        string format,
        int quality,
        bool hasClip,
        ScreenshotClip clip,
        bool captureBeyondViewport,
        bool fromSurface,
        bool omitBackground)
    {
        _format = format;
        _quality = quality;
        _hasClip = hasClip;
        _clip = clip;
        _captureBeyondViewport = captureBeyondViewport;
        _fromSurface = fromSurface;
        _omitBackground = omitBackground;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("format"u8);
        writer.StringValue(_format);

        if (_quality > 0)
        {
            writer.Name("quality"u8);
            writer.Int32Value(_quality);
        }

        if (_hasClip)
        {
            writer.Name("clip"u8);
            writer.BeginObject();
            writer.Name("x"u8);
            writer.DoubleValue(_clip.X);
            writer.Name("y"u8);
            writer.DoubleValue(_clip.Y);
            writer.Name("width"u8);
            writer.DoubleValue(_clip.Width);
            writer.Name("height"u8);
            writer.DoubleValue(_clip.Height);
            writer.Name("scale"u8);
            writer.DoubleValue(_clip.Scale);
            writer.EndObject();
        }

        writer.Name("captureBeyondViewport"u8);
        writer.BoolValue(_captureBeyondViewport);
        writer.Name("fromSurface"u8);
        writer.BoolValue(_fromSurface);
        writer.Name("omitBackground"u8);
        writer.BoolValue(_omitBackground);
    }
}

/// <summary>Result of <c>Page.captureScreenshot</c> with the base64 payload decoded to bytes.</summary>
public readonly struct ScreenshotResult : ICdpResult<ScreenshotResult>
{
    public ReadOnlyMemory<byte> Data { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out ScreenshotResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("data"u8))
        {
            return false;
        }

        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return false;
        }

        byte[]? unescaped = null;
        if (escaped)
        {
            unescaped = new byte[raw.Length];
            if (!JsonUtf8.TryUnescape(raw, unescaped, out int unescapedLength))
            {
                return false;
            }

            raw = unescaped.AsSpan(0, unescapedLength);
        }

        try
        {
            byte[] decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(raw.Length)];
            if (Base64.DecodeFromUtf8(raw, decoded, out _, out int written) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            value = new ScreenshotResult { Data = decoded.AsMemory(0, written) };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Result of <c>Page.getLayoutMetrics</c>: document content size and CSS viewport size.</summary>
public readonly struct LayoutMetricsResult : ICdpResult<LayoutMetricsResult>
{
    public double Width { get; init; }

    public double Height { get; init; }

    /// <summary>Layout viewport width in CSS pixels (the iframe's own width when frame-scoped).</summary>
    public double ViewportWidth { get; init; }

    /// <summary>Layout viewport height in CSS pixels.</summary>
    public double ViewportHeight { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out LayoutMetricsResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        double width = 0;
        double height = 0;
        double viewportWidth = 0;
        double viewportHeight = 0;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if ((name.SequenceEqual("cssContentSize"u8) || name.SequenceEqual("contentSize"u8))
                && cursor.TryEnterObject())
            {
                double candidateWidth = 0;
                double candidateHeight = 0;

                while (cursor.TryNextProperty(out ReadOnlySpan<byte> sizeName))
                {
                    if (sizeName.SequenceEqual("width"u8))
                    {
                        cursor.TryGetDouble(out candidateWidth);
                    }
                    else if (sizeName.SequenceEqual("height"u8))
                    {
                        cursor.TryGetDouble(out candidateHeight);
                    }
                    else if (!cursor.SkipValue())
                    {
                        return false;
                    }
                }

                // cssContentSize wins when present; contentSize is the fallback for older browsers.
                if (name.SequenceEqual("cssContentSize"u8) || width == 0 || height == 0)
                {
                    width = candidateWidth;
                    height = candidateHeight;
                }
            }
            else if ((name.SequenceEqual("cssLayoutViewport"u8) || name.SequenceEqual("layoutViewport"u8))
                && cursor.TryEnterObject())
            {
                double candidateWidth = 0;
                double candidateHeight = 0;

                while (cursor.TryNextProperty(out ReadOnlySpan<byte> sizeName))
                {
                    if (sizeName.SequenceEqual("clientWidth"u8))
                    {
                        cursor.TryGetDouble(out candidateWidth);
                    }
                    else if (sizeName.SequenceEqual("clientHeight"u8))
                    {
                        cursor.TryGetDouble(out candidateHeight);
                    }
                    else if (!cursor.SkipValue())
                    {
                        return false;
                    }
                }

                if (name.SequenceEqual("cssLayoutViewport"u8) || viewportWidth == 0 || viewportHeight == 0)
                {
                    viewportWidth = candidateWidth;
                    viewportHeight = candidateHeight;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new LayoutMetricsResult
        {
            Width = width,
            Height = height,
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
        };
        return width > 0 && height > 0;
    }
}

/// <summary>Parameters of <c>Page.handleJavaScriptDialog</c>.</summary>
public readonly struct HandleJavaScriptDialogParams : ICdpCommandParams
{
    private readonly bool _accept;
    private readonly string? _promptText;

    public HandleJavaScriptDialogParams(bool accept, string? promptText)
    {
        _accept = accept;
        _promptText = promptText;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("accept"u8);
        writer.BoolValue(_accept);

        if (_promptText is not null)
        {
            writer.Name("promptText"u8);
            writer.StringValue(_promptText);
        }
    }
}

/// <summary>Parameters of <c>Target.getTargetInfo</c>.</summary>
public readonly struct GetTargetInfoParams : ICdpCommandParams
{
    private readonly string _targetId;

    public GetTargetInfoParams(string targetId) => _targetId = targetId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("targetId"u8);
        writer.StringValue(_targetId);
    }
}

/// <summary>Result of <c>Target.getTargetInfo</c>.</summary>
public readonly struct TargetInfoResult : ICdpResult<TargetInfoResult>
{
    public string Title { get; init; }

    public string Url { get; init; }

    public string Type { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out TargetInfoResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("targetInfo"u8) || !cursor.TryEnterObject())
        {
            return false;
        }

        string title = string.Empty;
        string url = string.Empty;
        string type = string.Empty;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("title"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawTitle, out _))
            {
                title = Encoding.UTF8.GetString(rawTitle);
            }
            else if (name.SequenceEqual("url"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawUrl, out _))
            {
                url = Encoding.UTF8.GetString(rawUrl);
            }
            else if (name.SequenceEqual("type"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawType, out _))
            {
                type = Encoding.UTF8.GetString(rawType);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new TargetInfoResult { Title = title, Url = url, Type = type };
        return true;
    }
}

/// <summary>Parameters of <c>Page.setWebLifecycleState</c>.</summary>
public readonly struct SetWebLifecycleStateParams : ICdpCommandParams
{
    private readonly string _state;

    public SetWebLifecycleStateParams(string state) => _state = state;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("state"u8);
        writer.StringValue(_state);
    }
}
