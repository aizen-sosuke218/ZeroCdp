using System.Buffers;
using System.Buffers.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Page.startScreencast</c>.</summary>
public readonly struct StartScreencastParams : ICdpCommandParams
{
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private readonly int _quality;
    private readonly int _everyNthFrame;

    public StartScreencastParams(
        int maxWidth,
        int maxHeight,
        int quality = 80,
        int everyNthFrame = 1)
    {
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _quality = quality;
        _everyNthFrame = everyNthFrame;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("format"u8);
        writer.StringValue("jpeg");
        writer.Name("quality"u8);
        writer.Int32Value(_quality);
        writer.Name("maxWidth"u8);
        writer.Int32Value(_maxWidth);
        writer.Name("maxHeight"u8);
        writer.Int32Value(_maxHeight);
        writer.Name("everyNthFrame"u8);
        writer.Int32Value(_everyNthFrame);
    }
}

/// <summary>Parameters of <c>Page.screencastFrameAck</c>.</summary>
public readonly struct ScreencastFrameAckParams : ICdpCommandParams
{
    private readonly int _sessionId;

    public ScreencastFrameAckParams(int sessionId) => _sessionId = sessionId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("sessionId"u8);
        writer.Int32Value(_sessionId);
    }
}

/// <summary>A JPEG frame emitted by <c>Page.screencastFrame</c>.</summary>
public readonly struct ScreencastFrameInfo
{
    public ReadOnlyMemory<byte> Data { get; init; }

    public int SessionId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> paramsUtf8, out ScreencastFrameInfo value)
    {
        value = default;
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        ReadOnlyMemory<byte> data = default;
        int sessionId = -1;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("data"u8))
            {
                if (!TryReadBase64(ref cursor, out data))
                {
                    return false;
                }
            }
            else if (property.SequenceEqual("sessionId"u8))
            {
                if (!cursor.TryGetInt32(out sessionId))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (data.Length == 0 || sessionId < 0)
        {
            return false;
        }

        value = new ScreencastFrameInfo { Data = data, SessionId = sessionId };
        return true;
    }

    private static bool TryReadBase64(ref JsonCursor cursor, out ReadOnlyMemory<byte> data)
    {
        data = default;
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
            if (Base64.DecodeFromUtf8(raw, decoded, out _, out int written)
                != OperationStatus.Done)
            {
                return false;
            }

            data = decoded.AsMemory(0, written);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
