using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Network.getResponseBody</c>.</summary>
public readonly struct GetResponseBodyParams : ICdpCommandParams
{
    private readonly string _requestId;

    public GetResponseBodyParams(string requestId) => _requestId = requestId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("requestId"u8);
        writer.StringValue(_requestId);
    }
}

/// <summary>Result of <c>Network.getResponseBody</c>.</summary>
public readonly struct ResponseBodyResult : ICdpResult<ResponseBodyResult>
{
    public string Body { get; init; }

    public bool Base64Encoded { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out ResponseBodyResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string body = string.Empty;
        bool base64 = false;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("body"u8))
            {
                body = CdpHeader.ReadString(ref cursor);
            }
            else if (name.SequenceEqual("base64Encoded"u8))
            {
                cursor.TryGetBoolean(out base64);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new ResponseBodyResult { Body = body, Base64Encoded = base64 };
        return true;
    }
}

/// <summary>A response body decoded to bytes and, when textual, UTF-8 text.</summary>
public sealed class NetworkBody
{
    public NetworkBody(byte[] data, bool base64Encoded)
    {
        Data = data;
        Base64Encoded = base64Encoded;
        Text = Encoding.UTF8.GetString(data);
    }

    public byte[] Data { get; }

    public string Text { get; }

    public bool Base64Encoded { get; }
}
