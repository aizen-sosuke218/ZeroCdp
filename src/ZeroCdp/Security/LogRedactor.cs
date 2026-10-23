using System.Buffers;
using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Security;

/// <summary>
/// Redacts credential-bearing values from CDP JSON before it is written to a log.
/// </summary>
/// <remarks>
/// <para>
/// The redactor is an allow-list in spirit: only values whose property name is known to carry a
/// secret are masked, and every other byte is copied as-is. Objects and arrays are walked, so a
/// token nested inside <c>params.headers</c> is still caught.
/// </para>
/// <para>
/// Malformed input is never returned raw. If the JSON cannot be parsed the result is a fixed
/// placeholder, because a partially redacted frame is not safe to log.
/// </para>
/// </remarks>
public static class LogRedactor
{
    private const int MaxDepth = 64;

    /// <summary>Replacement written in place of a sensitive value.</summary>
    public static ReadOnlySpan<byte> Mask => "\"***\""u8;

    /// <summary>Redacts a complete CDP command or event frame.</summary>
    public static string RedactFrame(ReadOnlySpan<byte> frame)
    {
        bool redactValueKey = IsInputText(frame) || IsSetCookie(frame);
        return Redact(frame, redactValueKey, "<invalid-cdp-frame>", requireObject: true);
    }

    /// <summary>Redacts a standalone JSON document.</summary>
    public static string RedactJson(ReadOnlySpan<byte> json)
        => Redact(json, redactValueKey: false, "<invalid-json>", requireObject: false);

    /// <summary>True when a property name is treated as a credential at any nesting level.</summary>
    public static bool IsSensitiveKey(ReadOnlySpan<byte> keyUtf8)
        => AsciiEqualsIgnoreCase(keyUtf8, "password"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "passwd"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "pwd"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "cookie"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "cookies"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "set-cookie"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "setCookie"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "authorization"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "proxy-authorization"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "token"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "tokens"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "access_token"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "refresh_token"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "accessToken"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "refreshToken"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "client_secret"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "secret"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "api_key"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "apiKey"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "x-api-key"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "private_key"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "privateKey"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "credential"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "credentials"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "otp"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "pin"u8)
            || AsciiEqualsIgnoreCase(keyUtf8, "promptText"u8);

    private static bool IsInputText(ReadOnlySpan<byte> frame)
        => MethodEquals(frame, "Input.insertText"u8);

    private static bool IsSetCookie(ReadOnlySpan<byte> frame)
        => MethodEquals(frame, "Network.setCookie"u8)
            || MethodEquals(frame, "Network.setCookies"u8)
            || MethodEquals(frame, "Storage.setCookies"u8);

    private static bool MethodEquals(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> method)
    {
        JsonCursor cursor = new(frame);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("method"u8))
        {
            return false;
        }

        return cursor.TryReadString(out ReadOnlySpan<byte> value, out _) && value.SequenceEqual(method);
    }

    private static string Redact(ReadOnlySpan<byte> json, bool redactValueKey, string invalid, bool requireObject)
    {
        if (requireObject)
        {
            JsonCursor probe = new(json);
            if (!probe.TryEnterObject())
            {
                return invalid;
            }
        }

        ArrayBufferWriter<byte> output = new(json.Length > 0 ? json.Length : 16);
        JsonCursor cursor = new(json);
        if (!TryRedactValue(ref cursor, output, depth: 0, redactValueKey))
        {
            return invalid;
        }

        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private static bool TryRedactValue(
        ref JsonCursor cursor,
        ArrayBufferWriter<byte> output,
        int depth,
        bool redactValueKey)
    {
        if (depth > MaxDepth)
        {
            if (!cursor.SkipValue())
            {
                return false;
            }

            Write(output, "\"<max-depth>\""u8);
            return true;
        }

        switch (cursor.Peek())
        {
            case JsonTokenKind.ObjectStart:
                return TryRedactObject(ref cursor, output, depth, redactValueKey);
            case JsonTokenKind.ArrayStart:
                return TryRedactArray(ref cursor, output, depth, redactValueKey);
            case JsonTokenKind.None:
                return false;
            default:
                return TryCopyPrimitive(ref cursor, output);
        }
    }

    private static bool TryRedactObject(
        ref JsonCursor cursor,
        ArrayBufferWriter<byte> output,
        int depth,
        bool redactValueKey)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        Write(output, "{"u8);
        bool first = true;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (!first)
            {
                Write(output, ","u8);
            }

            first = false;
            Write(output, "\""u8);
            Write(output, name);
            Write(output, "\":"u8);

            bool sensitive = IsSensitiveKey(name)
                || (redactValueKey && AsciiEqualsIgnoreCase(name, "value"u8));

            if (sensitive)
            {
                if (!cursor.SkipValue())
                {
                    return false;
                }

                Write(output, Mask);
            }
            else if (!TryRedactValue(ref cursor, output, depth + 1, redactValueKey))
            {
                return false;
            }
        }

        Write(output, "}"u8);
        return true;
    }

    private static bool TryRedactArray(
        ref JsonCursor cursor,
        ArrayBufferWriter<byte> output,
        int depth,
        bool redactValueKey)
    {
        if (!cursor.TryEnterArray())
        {
            return false;
        }

        Write(output, "["u8);
        bool first = true;
        while (cursor.TryNextArrayElement())
        {
            if (!first)
            {
                Write(output, ","u8);
            }

            first = false;
            if (!TryRedactValue(ref cursor, output, depth + 1, redactValueKey))
            {
                return false;
            }
        }

        Write(output, "]"u8);
        return true;
    }

    private static bool TryCopyPrimitive(ref JsonCursor cursor, ArrayBufferWriter<byte> output)
    {
        int start = cursor.Position;
        bool valid = cursor.Peek() switch
        {
            JsonTokenKind.Text => cursor.TryReadString(out _, out _),
            JsonTokenKind.True or JsonTokenKind.False => cursor.TryGetBoolean(out _),
            JsonTokenKind.Null => cursor.TryGetNull(),
            JsonTokenKind.Number => cursor.TryReadNumber(out ReadOnlySpan<byte> number) && IsValidJsonNumber(number),
            _ => false,
        };

        if (!valid)
        {
            return false;
        }

        Write(output, cursor.Utf8[start..cursor.Position]);
        return true;
    }

    /// <summary>Strict RFC 8259 number grammar; a bare word is not a JSON primitive.</summary>
    private static bool IsValidJsonNumber(ReadOnlySpan<byte> number)
    {
        int i = 0;
        if (i < number.Length && number[i] == (byte)'-')
        {
            i++;
        }

        if (i >= number.Length)
        {
            return false;
        }

        if (number[i] == (byte)'0')
        {
            i++;
        }
        else if (number[i] is >= (byte)'1' and <= (byte)'9')
        {
            i++;
            while (i < number.Length && char.IsAsciiDigit((char)number[i]))
            {
                i++;
            }
        }
        else
        {
            return false;
        }

        if (i < number.Length && number[i] == (byte)'.')
        {
            i++;
            int digits = i;
            while (i < number.Length && char.IsAsciiDigit((char)number[i]))
            {
                i++;
            }

            if (i == digits)
            {
                return false;
            }
        }

        if (i < number.Length && number[i] is (byte)'e' or (byte)'E')
        {
            i++;
            if (i < number.Length && number[i] is (byte)'+' or (byte)'-')
            {
                i++;
            }

            int digits = i;
            while (i < number.Length && char.IsAsciiDigit((char)number[i]))
            {
                i++;
            }

            if (i == digits)
            {
                return false;
            }
        }

        return i == number.Length;
    }

    private static void Write(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
        => output.Write(value);

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value)
        => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
}
