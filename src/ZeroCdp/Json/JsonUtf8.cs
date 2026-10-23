namespace ZeroCdp.Json;

/// <summary>UTF-8 JSON encoding helpers used by the command writer and result readers.</summary>
public static class JsonUtf8
{
    /// <summary>Worst case expansion for a single UTF-16 char: <c>\uD83D\uDE00</c> is 12 bytes for a surrogate pair.</summary>
    private const int MaxEscapedChar = 6;

    /// <summary>Upper bound on the UTF-8 byte count produced when escaping <paramref name="length"/> UTF-16 chars.</summary>
    public static int MaxEscapedLength(int length) => (length * MaxEscapedChar) + 2;

    /// <summary>
    /// Writes <paramref name="value"/> as a quoted JSON string, escaping per RFC 8259.
    /// Non-ASCII text is emitted as raw UTF-8; only the mandatory escapes are used.
    /// </summary>
    /// <returns><see langword="false"/> if <paramref name="destination"/> is too small.</returns>
    public static bool TryWriteEscaped(Span<byte> destination, ReadOnlySpan<char> value, out int written)
    {
        written = 0;
        if (destination.Length < MaxEscapedLength(value.Length))
        {
            return false;
        }

        int w = 0;
        destination[w++] = (byte)'"';

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'"';
                    continue;
                case '\\':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'\\';
                    continue;
                case '\b':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'b';
                    continue;
                case '\f':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'f';
                    continue;
                case '\n':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'n';
                    continue;
                case '\r':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'r';
                    continue;
                case '\t':
                    destination[w++] = (byte)'\\';
                    destination[w++] = (byte)'t';
                    continue;
                default:
                    break;
            }

            if (c < 0x20)
            {
                w += WriteUnicodeEscape(destination[w..], c);
                continue;
            }

            if (c < 0x80)
            {
                destination[w++] = (byte)c;
                continue;
            }

            // 2-byte / 3-byte sequences are written directly; surrogates are paired below.
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                w += EncodeUtf8(destination[w..], char.ConvertToUtf32(c, value[i + 1]));
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                w += WriteUnicodeEscape(destination[w..], c);
                continue;
            }

            w += EncodeUtf8(destination[w..], c);
        }

        destination[w++] = (byte)'"';
        written = w;
        return true;
    }

    /// <summary>
    /// Resolves JSON escapes into UTF-8. <paramref name="source"/> must be the raw string body
    /// returned by <see cref="JsonCursor.TryReadString"/>.
    /// </summary>
    public static bool TryUnescape(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        int w = 0;
        for (int i = 0; i < source.Length; i++)
        {
            byte b = source[i];
            if (b != (byte)'\\')
            {
                if (w >= destination.Length)
                {
                    return false;
                }

                destination[w++] = b;
                continue;
            }

            i++;
            if (i >= source.Length)
            {
                return false;
            }

            byte esc = source[i];
            byte literal = esc switch
            {
                (byte)'"' => (byte)'"',
                (byte)'\\' => (byte)'\\',
                (byte)'/' => (byte)'/',
                (byte)'b' => (byte)'\b',
                (byte)'f' => (byte)'\f',
                (byte)'n' => (byte)'\n',
                (byte)'r' => (byte)'\r',
                (byte)'t' => (byte)'\t',
                _ => 0,
            };

            if (literal != 0)
            {
                if (w >= destination.Length)
                {
                    return false;
                }

                destination[w++] = literal;
                continue;
            }

            if (esc != (byte)'u' || i + 4 >= source.Length)
            {
                return false;
            }

            if (!TryParseHex4(source.Slice(i + 1, 4), out int code))
            {
                return false;
            }

            i += 4;

            if (char.IsHighSurrogate((char)code) && i + 6 < source.Length && source[i + 1] == (byte)'\\' && source[i + 2] == (byte)'u')
            {
                if (TryParseHex4(source.Slice(i + 3, 4), out int low) && char.IsLowSurrogate((char)low))
                {
                    i += 6;
                    code = char.ConvertToUtf32((char)code, (char)low);
                }
            }

            int needed = code < 0x80 ? 1 : code < 0x800 ? 2 : code < 0x10000 ? 3 : 4;
            if (w + needed > destination.Length)
            {
                return false;
            }

            w += EncodeUtf8(destination[w..], code);
        }

        written = w;
        return true;
    }

    private static int WriteUnicodeEscape(Span<byte> destination, int code)
    {
        const string hex = "0123456789abcdef";
        destination[0] = (byte)'\\';
        destination[1] = (byte)'u';
        destination[2] = (byte)hex[(code >> 12) & 0xF];
        destination[3] = (byte)hex[(code >> 8) & 0xF];
        destination[4] = (byte)hex[(code >> 4) & 0xF];
        destination[5] = (byte)hex[code & 0xF];
        return 6;
    }

    private static bool TryParseHex4(ReadOnlySpan<byte> source, out int value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            int digit = source[i] switch
            {
                >= (byte)'0' and <= (byte)'9' => source[i] - '0',
                >= (byte)'a' and <= (byte)'f' => source[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => source[i] - 'A' + 10,
                _ => -1,
            };

            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static int EncodeUtf8(Span<byte> destination, int codePoint)
    {
        if (codePoint < 0x80)
        {
            destination[0] = (byte)codePoint;
            return 1;
        }

        if (codePoint < 0x800)
        {
            destination[0] = (byte)(0xC0 | (codePoint >> 6));
            destination[1] = (byte)(0x80 | (codePoint & 0x3F));
            return 2;
        }

        if (codePoint < 0x10000)
        {
            destination[0] = (byte)(0xE0 | (codePoint >> 12));
            destination[1] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
            destination[2] = (byte)(0x80 | (codePoint & 0x3F));
            return 3;
        }

        destination[0] = (byte)(0xF0 | (codePoint >> 18));
        destination[1] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
        destination[2] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
        destination[3] = (byte)(0x80 | (codePoint & 0x3F));
        return 4;
    }
}
