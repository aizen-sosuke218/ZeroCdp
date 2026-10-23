using System.Text;
using ZeroCdp.Json;
using Xunit;

namespace ZeroCdp.Tests;

public class JsonCursorTests
{
    [Fact]
    public void Skips_nested_values_iteratively()
    {
        ReadOnlySpan<byte> json =
            """{"id":1,"result":{"a":[1,2,{"b":"c]}"}],"d":true},"sessionId":"S1"}"""u8;

        JsonCursor cursor = new(json);
        Assert.True(cursor.TryEnterObject());
        Assert.True(cursor.TryNextProperty(out ReadOnlySpan<byte> first));
        Assert.Equal("id", Encoding.UTF8.GetString(first));
        Assert.True(cursor.TryGetInt64(out long id));
        Assert.Equal(1, id);

        // The nested object contains "]}" inside a string; a naive scanner would desync here.
        Assert.True(cursor.TryNextProperty(out ReadOnlySpan<byte> second));
        Assert.Equal("result", Encoding.UTF8.GetString(second));
        Assert.True(cursor.SkipValue());

        Assert.True(cursor.TryNextProperty(out ReadOnlySpan<byte> third));
        Assert.Equal("sessionId", Encoding.UTF8.GetString(third));
        Assert.True(cursor.TryReadString(out ReadOnlySpan<byte> session, out _));
        Assert.Equal("S1", Encoding.UTF8.GetString(session));
        Assert.False(cursor.TryNextProperty(out _));
    }

    [Fact]
    public void Reads_top_level_members_in_one_pass()
    {
        ReadOnlySpan<byte> json =
            """{"id":42,"result":{"product":"Chrome/140"},"sessionId":"S1"}"""u8;

        JsonCursor cursor = new(json);
        Assert.True(cursor.TryEnterObject());

        long id = -1;
        ReadOnlySpan<byte> result = default;
        ReadOnlySpan<byte> sessionId = default;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (Utf8Equals(name, "id"u8))
            {
                Assert.True(cursor.TryGetInt64(out id));
            }
            else if (Utf8Equals(name, "result"u8))
            {
                int start = cursor.Position;
                Assert.True(cursor.SkipValue());
                result = json[start..cursor.Position];
            }
            else if (Utf8Equals(name, "sessionId"u8))
            {
                Assert.True(cursor.TryReadString(out sessionId, out bool escaped));
                Assert.False(escaped);
            }
            else
            {
                Assert.True(cursor.SkipValue());
            }
        }

        Assert.Equal(42, id);
        Assert.Equal("S1", Encoding.UTF8.GetString(sessionId));
        Assert.Equal("""{"product":"Chrome/140"}""", Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Seeks_property_without_materialising_strings()
    {
        ReadOnlySpan<byte> json = """{"a":{"deep":[1,2,3]},"target":7}"""u8;
        JsonCursor cursor = new(json);
        Assert.True(cursor.TryEnterObject());
        Assert.True(cursor.TrySeekProperty("target"u8));
        Assert.True(cursor.TryGetInt64(out long value));
        Assert.Equal(7, value);
    }

    [Fact]
    public void Reports_missing_property_and_restores_position()
    {
        ReadOnlySpan<byte> json = """{"a":1}"""u8;
        JsonCursor cursor = new(json);
        Assert.True(cursor.TryEnterObject());
        int before = cursor.Position;
        Assert.False(cursor.TrySeekProperty("missing"u8));
        Assert.Equal(before, cursor.Position);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("quote\"backslash\\slash/")]
    [InlineData("newline\nand\ttab")]
    [InlineData("unicode: \u00e9 \u1ea1 \ud83d\ude00")]
    [InlineData("control:\u0001\u001f")]
    public void Escapes_and_unescapes_round_trip(string value)
    {
        byte[] escaped = new byte[JsonUtf8.MaxEscapedLength(value.Length)];
        Assert.True(JsonUtf8.TryWriteEscaped(escaped, value, out int escapedLength));

        ReadOnlySpan<byte> body = escaped.AsSpan(1, escapedLength - 2);
        byte[] unescaped = new byte[body.Length];
        Assert.True(JsonUtf8.TryUnescape(body, unescaped, out int unescapedLength));

        Assert.Equal(value, Encoding.UTF8.GetString(unescaped, 0, unescapedLength));
    }

    [Fact]
    public void Escaped_strings_are_valid_json()
    {
        byte[] escaped = new byte[256];
        Assert.True(JsonUtf8.TryWriteEscaped(escaped, "a\"b\\c\nd", out int length));

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(escaped.AsMemory(0, length));
        Assert.Equal("a\"b\\c\nd", document.RootElement.GetString());
    }

    private static bool Utf8Equals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);
}
