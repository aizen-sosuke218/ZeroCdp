using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Target.setAutoAttach</c>.</summary>
public readonly struct SetAutoAttachParams : ICdpCommandParams
{
    private readonly bool _autoAttach;
    private readonly bool _waitForDebuggerOnStart;
    private readonly bool _flatten;

    public SetAutoAttachParams(bool autoAttach, bool waitForDebuggerOnStart, bool flatten)
    {
        _autoAttach = autoAttach;
        _waitForDebuggerOnStart = waitForDebuggerOnStart;
        _flatten = flatten;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("autoAttach"u8);
        writer.BoolValue(_autoAttach);
        writer.Name("waitForDebuggerOnStart"u8);
        writer.BoolValue(_waitForDebuggerOnStart);
        // Flattened sessions arrive on the same connection, tagged with a sessionId. That is what
        // lets one socket carry every page without a second transport per target.
        writer.Name("flatten"u8);
        writer.BoolValue(_flatten);
    }
}

/// <summary>Parameters of <c>Target.createTarget</c>.</summary>
public readonly struct CreateTargetParams : ICdpCommandParams
{
    private readonly string _url;
    private readonly string? _browserContextId;

    public CreateTargetParams(string url, string? browserContextId = null)
    {
        _url = url;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("url"u8);
        writer.StringValue(_url);

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}

/// <summary>Result of <c>Target.createTarget</c>.</summary>
public readonly struct TargetCreated : ICdpResult<TargetCreated>
{
    public string? TargetId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out TargetCreated value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string? targetId = null;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("targetId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                targetId = Encoding.UTF8.GetString(raw);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new TargetCreated { TargetId = targetId };
        return true;
    }
}

/// <summary>Parameters of <c>Target.attachToTarget</c>.</summary>
public readonly struct AttachToTargetParams : ICdpCommandParams
{
    private readonly string _targetId;
    private readonly bool _flatten;

    public AttachToTargetParams(string targetId, bool flatten)
    {
        _targetId = targetId;
        _flatten = flatten;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("targetId"u8);
        writer.StringValue(_targetId);
        writer.Name("flatten"u8);
        writer.BoolValue(_flatten);
    }
}

/// <summary>Result of <c>Target.attachToTarget</c>.</summary>
public readonly struct TargetAttached : ICdpResult<TargetAttached>
{
    public string? SessionId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out TargetAttached value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string? sessionId = null;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("sessionId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                sessionId = Encoding.UTF8.GetString(raw);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new TargetAttached { SessionId = sessionId };
        return true;
    }
}

/// <summary>Parameters of <c>Target.closeTarget</c>.</summary>
public readonly struct CloseTargetParams : ICdpCommandParams
{
    private readonly string _targetId;

    public CloseTargetParams(string targetId) => _targetId = targetId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("targetId"u8);
        writer.StringValue(_targetId);
    }
}

/// <summary>Parameters of <c>Target.detachFromTarget</c>.</summary>
public readonly struct DetachSessionParams : ICdpCommandParams
{
    private readonly ReadOnlyMemory<byte> _sessionIdUtf8;

    public DetachSessionParams(ReadOnlyMemory<byte> sessionIdUtf8) => _sessionIdUtf8 = sessionIdUtf8;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("sessionId"u8);
        writer.Utf8StringValue(_sessionIdUtf8.Span);
    }
}

/// <summary>Parameters of <c>Page.navigate</c>.</summary>
public readonly struct NavigateParams : ICdpCommandParams
{
    private readonly string _url;
    private readonly string? _frameId;

    public NavigateParams(string url, string? frameId = null)
    {
        _url = url;
        _frameId = frameId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("url"u8);
        writer.StringValue(_url);

        if (!string.IsNullOrEmpty(_frameId))
        {
            writer.Name("frameId"u8);
            writer.StringValue(_frameId);
        }
    }
}

/// <summary>Result of <c>Page.navigate</c>.</summary>
public readonly struct NavigateResult : ICdpResult<NavigateResult>
{
    public string? FrameId { get; init; }

    public string? LoaderId { get; init; }

    public string? ErrorText { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out NavigateResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string? frameId = null;
        string? loaderId = null;
        string? errorText = null;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("frameId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawFrame, out _))
            {
                frameId = Encoding.UTF8.GetString(rawFrame);
            }
            else if (name.SequenceEqual("loaderId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawLoader, out _))
            {
                loaderId = Encoding.UTF8.GetString(rawLoader);
            }
            else if (name.SequenceEqual("errorText"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawError, out _))
            {
                errorText = Encoding.UTF8.GetString(rawError);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new NavigateResult { FrameId = frameId, LoaderId = loaderId, ErrorText = errorText };
        return true;
    }
}
