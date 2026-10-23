using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>Small JSON/JavaScript text helpers for expressions that need a quoted string.</summary>
internal static class JsText
{
    /// <summary>Returns <paramref name="value"/> as a quoted JSON string literal.</summary>
    public static string Quote(string value)
    {
        Span<byte> initial = stackalloc byte[Math.Min(JsonUtf8.MaxEscapedLength(value.Length), 512)];
        if (initial.Length >= JsonUtf8.MaxEscapedLength(value.Length)
            && JsonUtf8.TryWriteEscaped(initial, value, out int written))
        {
            return Encoding.UTF8.GetString(initial[..written]);
        }

        byte[] buffer = new byte[JsonUtf8.MaxEscapedLength(value.Length)];
        if (!JsonUtf8.TryWriteEscaped(buffer, value, out written))
        {
            throw new ArgumentException("The value could not be encoded as JSON.", nameof(value));
        }

        return Encoding.UTF8.GetString(buffer, 0, written);
    }

    /// <summary>Builds a CDP <c>CallArgument</c> array with one string argument per value.</summary>
    public static string StringArguments(IReadOnlyList<string> values)
    {
        StringBuilder builder = new(values.Count * 16);
        builder.Append('[');
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"value\":");
            builder.Append(Quote(values[i]));
            builder.Append('}');
        }

        builder.Append(']');
        return builder.ToString();
    }
}
