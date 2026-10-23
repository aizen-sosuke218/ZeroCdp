using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>One HTTP header name/value pair used by network and emulation commands.</summary>
public readonly struct CdpHeader
{
    public CdpHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    internal static void WriteArray(ref CommandWriter writer, ReadOnlySpan<byte> name, IReadOnlyList<CdpHeader> headers)
    {
        writer.Name(name);
        writer.Value("["u8);
        for (int i = 0; i < headers.Count; i++)
        {
            if (i > 0)
            {
                writer.Value(","u8);
            }

            CdpHeader header = headers[i];
            writer.BeginObject();
            writer.Name("name"u8);
            writer.StringValue(header.Name);
            writer.Name("value"u8);
            writer.StringValue(header.Value);
            writer.EndObject();
        }

        writer.Value("]"u8);
    }

    internal static void WriteObject(ref CommandWriter writer, ReadOnlySpan<byte> name, IReadOnlyList<CdpHeader> headers)
    {
        writer.Name(name);
        writer.BeginObject();
        for (int i = 0; i < headers.Count; i++)
        {
            writer.Name(Encoding.UTF8.GetBytes(headers[i].Name));
            writer.StringValue(headers[i].Value);
        }

        writer.EndObject();
    }

    internal static bool TryParseArray(ref JsonCursor cursor, List<CdpHeader> headers)
    {
        if (!cursor.TryEnterArray())
        {
            return false;
        }

        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            string headerName = string.Empty;
            string headerValue = string.Empty;
            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual("name"u8))
                {
                    headerName = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("value"u8))
                {
                    headerValue = ReadString(ref cursor);
                }
                else if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            headers.Add(new CdpHeader(headerName, headerValue));
        }

        return true;
    }

    internal static bool TryParseObject(ref JsonCursor cursor, List<CdpHeader> headers)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            string headerName = Encoding.UTF8.GetString(property);
            string headerValue = ReadString(ref cursor);
            headers.Add(new CdpHeader(headerName, headerValue));
        }

        return true;
    }

    internal static string ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            cursor.SkipValue();
            return string.Empty;
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }
}
