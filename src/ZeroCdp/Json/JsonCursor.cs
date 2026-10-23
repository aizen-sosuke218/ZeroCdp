using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ZeroCdp.Json;

public enum JsonTokenKind : byte
{
    None = 0,
    ObjectStart,
    ArrayStart,
    Text,
    Number,
    True,
    False,
    Null,
}

/// <summary>
/// A forward-only, allocation-free cursor over UTF-8 JSON.
/// </summary>
/// <remarks>
/// <para>
/// Slices returned by this cursor point into the original buffer and are only valid while
/// that buffer is alive. Nothing here materialises a <see cref="string"/>: it is the caller's
/// choice to copy bytes out.
/// </para>
/// <para>
/// Scanning is done with inline byte loops over an unmanaged reference rather than
/// <c>SearchValues</c>/<c>IndexOfAny</c>: CDP frames are small (tens to a few thousand bytes),
/// and for those sizes the fixed overhead of the vectorised helper costs more than the scan.
/// </para>
/// </remarks>
public ref struct JsonCursor
{
    private readonly ReadOnlySpan<byte> _utf8;
    private int _pos;

    public JsonCursor(ReadOnlySpan<byte> utf8)
    {
        _utf8 = utf8;
        _pos = 0;
    }

    public JsonCursor(ReadOnlySpan<byte> utf8, int position)
    {
        _utf8 = utf8;
        _pos = position;
    }

    public readonly ReadOnlySpan<byte> Utf8 => _utf8;

    public readonly int Position => _pos;

    public readonly bool End => _pos >= _utf8.Length;

    public void SkipWhitespace()
    {
        ref byte start = ref MemoryMarshal.GetReference(_utf8);
        int length = _utf8.Length;
        int i = _pos;
        while (i < length)
        {
            byte b = Unsafe.Add(ref start, i);
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                i++;
                continue;
            }

            break;
        }

        _pos = i;
    }

    public readonly JsonTokenKind Peek()
    {
        ref byte start = ref MemoryMarshal.GetReference(_utf8);
        int length = _utf8.Length;
        int i = _pos;
        while (i < length)
        {
            byte b = Unsafe.Add(ref start, i);
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return b switch
                {
                    (byte)'{' => JsonTokenKind.ObjectStart,
                    (byte)'[' => JsonTokenKind.ArrayStart,
                    (byte)'"' => JsonTokenKind.Text,
                    (byte)'t' => JsonTokenKind.True,
                    (byte)'f' => JsonTokenKind.False,
                    (byte)'n' => JsonTokenKind.Null,
                    _ => JsonTokenKind.Number,
                };
            }

            i++;
        }

        return JsonTokenKind.None;
    }

    public bool TryEnterObject()
    {
        SkipWhitespace();
        if (End || _utf8[_pos] != (byte)'{')
        {
            return false;
        }

        _pos++;
        return true;
    }

    public bool TryEnterArray()
    {
        SkipWhitespace();
        if (End || _utf8[_pos] != (byte)'[')
        {
            return false;
        }

        _pos++;
        return true;
    }

    /// <summary>
    /// Advances to the next element of the current array. On <see langword="true"/> the cursor
    /// sits on the element. On <see langword="false"/> the closing bracket was consumed.
    /// </summary>
    public bool TryNextArrayElement()
    {
        SkipWhitespace();
        if (End)
        {
            return false;
        }

        byte b = _utf8[_pos];
        if (b == (byte)',')
        {
            _pos++;
            SkipWhitespace();
        }

        if (End)
        {
            return false;
        }

        if (_utf8[_pos] == (byte)']')
        {
            _pos++;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Advances to the next property of the current object. On <see langword="true"/> the
    /// cursor sits on the property value. On <see langword="false"/> the closing brace was consumed.
    /// </summary>
    /// <param name="name">Raw UTF-8 bytes of the property name (unescaped names are the norm in CDP).</param>
    public bool TryNextProperty(out ReadOnlySpan<byte> name)
    {
        name = default;
        SkipWhitespace();
        if (End)
        {
            return false;
        }

        byte b = _utf8[_pos];
        if (b == (byte)'}')
        {
            _pos++;
            return false;
        }

        if (b == (byte)',')
        {
            _pos++;
            SkipWhitespace();
        }

        if (End || _utf8[_pos] != (byte)'"')
        {
            return false;
        }

        if (!TryReadString(out name, out _))
        {
            return false;
        }

        SkipWhitespace();
        if (End || _utf8[_pos] != (byte)':')
        {
            return false;
        }

        _pos++;
        SkipWhitespace();
        return true;
    }

    /// <summary>
    /// Positions the cursor on the value of <paramref name="name"/>. Accepts only unescaped,
    /// ASCII-compatible names, which is every property in the DevTools protocol.
    /// </summary>
    public bool TrySeekProperty(ReadOnlySpan<byte> name)
    {
        int start = _pos;
        while (TryNextProperty(out ReadOnlySpan<byte> candidate))
        {
            if (candidate.SequenceEqual(name))
            {
                return true;
            }

            if (!SkipValue())
            {
                break;
            }
        }

        _pos = start;
        return false;
    }

    /// <summary>Reads a JSON string. <paramref name="escaped"/> is set when the slice still contains escapes.</summary>
    public bool TryReadString(out ReadOnlySpan<byte> value, out bool escaped)
    {
        value = default;
        escaped = false;
        SkipWhitespace();
        if (End || _utf8[_pos] != (byte)'"')
        {
            return false;
        }

        ref byte start = ref MemoryMarshal.GetReference(_utf8);
        int length = _utf8.Length;
        int i = _pos + 1;
        int bodyStart = i;

        while (i < length)
        {
            byte b = Unsafe.Add(ref start, i);
            if (b == (byte)'"')
            {
                value = _utf8[bodyStart..i];
                _pos = i + 1;
                return true;
            }

            if (b == (byte)'\\')
            {
                // A backslash escapes exactly one byte; the \uXXXX digits that may follow contain
                // neither a quote nor a backslash, so stepping two bytes stays in sync.
                escaped = true;
                i += 2;
                continue;
            }

            i++;
        }

        _pos = length;
        return false;
    }

    public bool TryReadNumber(out ReadOnlySpan<byte> value)
    {
        value = default;
        SkipWhitespace();
        ref byte start = ref MemoryMarshal.GetReference(_utf8);
        int length = _utf8.Length;
        int i = _pos;
        while (i < length)
        {
            byte b = Unsafe.Add(ref start, i);
            if (b is (byte)',' or (byte)'}' or (byte)']' or (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                break;
            }

            i++;
        }

        if (i == _pos)
        {
            return false;
        }

        value = _utf8[_pos..i];
        _pos = i;
        return true;
    }

    public bool TryGetInt64(out long value)
    {
        value = 0;
        if (!TryReadNumber(out ReadOnlySpan<byte> raw))
        {
            return false;
        }

        return Utf8Parser.TryParse(raw, out value, out int consumed) && consumed == raw.Length;
    }

    public bool TryGetInt32(out int value)
    {
        value = 0;
        if (!TryGetInt64(out long wide))
        {
            return false;
        }

        value = (int)wide;
        return true;
    }

    public bool TryGetDouble(out double value)
    {
        value = 0;
        if (!TryReadNumber(out ReadOnlySpan<byte> raw))
        {
            return false;
        }

        return Utf8Parser.TryParse(raw, out value, out int consumed) && consumed == raw.Length;
    }

    public bool TryGetBoolean(out bool value)
    {
        value = false;
        SkipWhitespace();
        if (_pos < _utf8.Length && _utf8[_pos] == (byte)'t' && _utf8[_pos..].StartsWith("true"u8))
        {
            _pos += 4;
            value = true;
            return true;
        }

        if (_pos < _utf8.Length && _utf8[_pos] == (byte)'f' && _utf8[_pos..].StartsWith("false"u8))
        {
            _pos += 5;
            return true;
        }

        return false;
    }

    public bool TryGetNull()
    {
        SkipWhitespace();
        if (_pos < _utf8.Length && _utf8[_pos..].StartsWith("null"u8))
        {
            _pos += 4;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Skips one value of any type. Implemented iteratively so a hostile or deep payload
    /// (a large DOM tree, for instance) can never overflow the stack.
    /// </summary>
    public bool SkipValue()
    {
        SkipWhitespace();
        if (End)
        {
            return false;
        }

        byte first = _utf8[_pos];
        if (first is (byte)'{' or (byte)'[')
        {
            ref byte start = ref MemoryMarshal.GetReference(_utf8);
            int length = _utf8.Length;
            int depth = 0;
            int i = _pos;

            while (i < length)
            {
                byte b = Unsafe.Add(ref start, i);
                switch (b)
                {
                    case (byte)'"':
                        i = SkipStringFrom(ref start, length, i + 1);
                        if (i < 0)
                        {
                            _pos = length;
                            return false;
                        }

                        continue;
                    case (byte)'{' or (byte)'[':
                        depth++;
                        i++;
                        continue;
                    case (byte)'}' or (byte)']':
                        depth--;
                        i++;
                        if (depth <= 0)
                        {
                            _pos = i;
                            return true;
                        }

                        continue;
                    default:
                        i++;
                        continue;
                }
            }

            _pos = length;
            return false;
        }

        if (first == (byte)'"')
        {
            return TrySkipString();
        }

        // number | true | false | null
        ref byte numberStart = ref MemoryMarshal.GetReference(_utf8);
        int numberLength = _utf8.Length;
        int n = _pos;
        while (n < numberLength)
        {
            byte b = Unsafe.Add(ref numberStart, n);
            if (b is (byte)',' or (byte)'}' or (byte)']' or (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                break;
            }

            n++;
        }

        _pos = n;
        return true;
    }

    private bool TrySkipString()
    {
        ref byte start = ref MemoryMarshal.GetReference(_utf8);
        int next = SkipStringFrom(ref start, _utf8.Length, _pos + 1);
        if (next < 0)
        {
            _pos = _utf8.Length;
            return false;
        }

        _pos = next;
        return true;
    }

    /// <summary>Returns the index just past the closing quote, or -1 when the string is unterminated.</summary>
    private static int SkipStringFrom(ref byte start, int length, int i)
    {
        while (i < length)
        {
            byte b = Unsafe.Add(ref start, i);
            if (b == (byte)'"')
            {
                return i + 1;
            }

            // Skips the escaped byte: a \u escape's hex digits contain no quote or backslash.
            i += b == (byte)'\\' ? 2 : 1;
        }

        return -1;
    }
}
