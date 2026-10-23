using System.Buffers;
using System.Buffers.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>
/// Builds a single CDP command frame directly into a pooled buffer, in UTF-8, without
/// intermediate strings or a JSON serializer.
/// </summary>
/// <remarks>
/// The produced frame is terminated with the NUL byte that the DevTools pipe transport requires.
/// The frame is only valid until the writer is disposed or reset.
/// </remarks>
public struct CommandWriter : IDisposable
{
    private byte[]? _buffer;
    private int _length;
    private bool _hasParams;

    public static CommandWriter Create(int initialCapacity = 512)
        => new() { _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 64)) };

    public void Begin(long id, ReadOnlySpan<byte> methodUtf8)
    {
        WriteRaw("{\"id\":"u8);
        WriteInt64(id);
        WriteRaw(",\"method\":\""u8);
        WriteRaw(methodUtf8);
        WriteRaw("\""u8);
        _hasParams = false;
    }

    public void BeginParams()
    {
        WriteRaw(",\"params\":{"u8);
        _hasParams = true;
    }

    /// <summary>Opens a nested JSON object. Pair with <see cref="EndObject"/>.</summary>
    public void BeginObject() => WriteRaw("{"u8);

    /// <summary>Closes the object opened by <see cref="BeginObject"/>.</summary>
    public void EndObject() => WriteRaw("}"u8);

    /// <summary>Writes <c>"name":</c>. Commas are derived from the previous byte.</summary>
    public void Name(ReadOnlySpan<byte> nameUtf8)
    {
        if (_length > 0)
        {
            byte last = _buffer![_length - 1];
            if (last != (byte)'{' && last != (byte)',')
            {
                WriteRaw(","u8);
            }
        }

        WriteRaw("\""u8);
        WriteRaw(nameUtf8);
        WriteRaw("\":"u8);
    }

    public void EndParams()
    {
        if (_hasParams)
        {
            WriteRaw("}"u8);
            _hasParams = false;
        }
    }

    public void SessionId(ReadOnlySpan<byte> sessionIdUtf8)
    {
        WriteRaw(",\"sessionId\":\""u8);
        WriteRaw(sessionIdUtf8);
        WriteRaw("\""u8);
    }

    public void StringValue(ReadOnlySpan<char> value)
    {
        Span<byte> span = GetSpan(JsonUtf8.MaxEscapedLength(value.Length));
        if (!JsonUtf8.TryWriteEscaped(span, value, out int written))
        {
            throw new InvalidOperationException("JSON string escaping overflowed the reserved span.");
        }

        Advance(written);
    }

    /// <summary>Writes UTF-8 bytes as a quoted JSON string, escaping only when required.</summary>
    public void Utf8StringValue(ReadOnlySpan<byte> value)
    {
        bool needsEscape = false;
        foreach (byte b in value)
        {
            if (b < 0x20 || b == (byte)'"' || b == (byte)'\\')
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
        {
            WriteRaw("\""u8);
            WriteRaw(value);
            WriteRaw("\""u8);
            return;
        }

        char[]? rented = null;
        Span<char> chars = value.Length <= 256
            ? stackalloc char[value.Length]
            : (rented = ArrayPool<char>.Shared.Rent(value.Length));

        int written = System.Text.Encoding.UTF8.GetChars(value, chars);
        StringValue(chars[..written]);

        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    public void Value(ReadOnlySpan<byte> rawJson)
    {
        WriteRaw(rawJson);
    }

    public void EmptyArray()
    {
        WriteRaw("[]"u8);
    }

    public void StringArray(IReadOnlyList<string> values)
    {
        WriteRaw("["u8);
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                WriteRaw(","u8);
            }

            StringValue(values[i]);
        }

        WriteRaw("]"u8);
    }

    public void EmptyObject()
    {
        WriteRaw("{}"u8);
    }

    public void Int32Value(int value) => WriteInt64(value);

    public void Int64Value(long value) => WriteInt64(value);

    public void DoubleValue(double value)
    {
        Span<byte> span = GetSpan(32);
        Utf8Formatter.TryFormat(value, span, out int written);
        Advance(written);
    }

    public void BoolValue(bool value) => WriteRaw(value ? "true"u8 : "false"u8);

    public void NullValue() => WriteRaw("null"u8);

    /// <summary>Closes the envelope and appends the NUL frame terminator.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        EndParams();
        WriteRaw("}\0"u8);
        return _buffer.AsSpan(0, _length);
    }

    public readonly ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>
    /// Same as <see cref="Finish"/> but as a <see cref="ReadOnlyMemory{Byte}"/>, so the frame can be
    /// handed to an async transport without copying it.
    /// </summary>
    public ReadOnlyMemory<byte> FinishMemory()
    {
        EndParams();
        WriteRaw("}\0"u8);
        return _buffer.AsMemory(0, _length);
    }

    public void Dispose()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        _length = 0;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void WriteInt64(long value)
    {
        Span<byte> span = GetSpan(20);
        Utf8Formatter.TryFormat(value, span, out int written);
        Advance(written);
    }

    private void WriteRaw(ReadOnlySpan<byte> utf8)
    {
        utf8.CopyTo(GetSpan(utf8.Length));
        Advance(utf8.Length);
    }

    private Span<byte> GetSpan(int sizeHint)
    {
        byte[]? buffer = _buffer ?? throw new ObjectDisposedException(nameof(CommandWriter));
        if (buffer.Length - _length < sizeHint)
        {
            Grow(sizeHint);
            buffer = _buffer!;
        }

        return buffer.AsSpan(_length);
    }

    private void Advance(int count) => _length += count;

    private void Grow(int sizeHint)
    {
        byte[] old = _buffer!;
        int next = Math.Max(old.Length * 2, _length + sizeHint);
        byte[] rented = ArrayPool<byte>.Shared.Rent(next);
        old.AsSpan(0, _length).CopyTo(rented);
        _buffer = rented;
        ArrayPool<byte>.Shared.Return(old);
    }
}
