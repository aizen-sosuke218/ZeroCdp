using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>
/// Implemented by structs that represent a typed CDP command result.
/// Results are parsed straight out of the receive buffer, so implementations must not retain
/// spans beyond the call.
/// </summary>
public interface ICdpResult<TSelf>
    where TSelf : struct, ICdpResult<TSelf>
{
    /// <summary>Parses <paramref name="resultUtf8"/>, the raw JSON object of the <c>result</c> member.</summary>
    static abstract bool TryParse(ReadOnlySpan<byte> resultUtf8, out TSelf value);
}

/// <summary>A command result with no payload.</summary>
public readonly struct Unit : ICdpResult<Unit>
{
    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out Unit value)
    {
        value = default;
        return true;
    }
}

/// <summary>Result of <c>Browser.getVersion</c>.</summary>
public struct BrowserVersion : ICdpResult<BrowserVersion>
{
    public ReadOnlyMemory<byte> ProtocolVersion { get; init; }

    public ReadOnlyMemory<byte> Product { get; init; }

    public ReadOnlyMemory<byte> Revision { get; init; }

    public ReadOnlyMemory<byte> UserAgent { get; init; }

    public ReadOnlyMemory<byte> JavaScriptVersion { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out BrowserVersion value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        ReadOnlyMemory<byte> protocolVersion = default;
        ReadOnlyMemory<byte> product = default;
        ReadOnlyMemory<byte> revision = default;
        ReadOnlyMemory<byte> userAgent = default;
        ReadOnlyMemory<byte> jsVersion = default;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("protocolVersion"u8))
            {
                protocolVersion = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("product"u8))
            {
                product = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("revision"u8))
            {
                revision = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("userAgent"u8))
            {
                userAgent = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("jsVersion"u8))
            {
                jsVersion = ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new BrowserVersion
        {
            ProtocolVersion = protocolVersion,
            Product = product,
            Revision = revision,
            UserAgent = userAgent,
            JavaScriptVersion = jsVersion,
        };
        return true;
    }

    private static ReadOnlyMemory<byte> ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return default;
        }

        if (!escaped)
        {
            return raw.ToArray();
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? buffer.AsMemory(0, written)
            : default;
    }
}
