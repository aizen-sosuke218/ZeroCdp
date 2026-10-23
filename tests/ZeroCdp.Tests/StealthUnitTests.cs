using ZeroCdp.Protocol;
using ZeroCdp.Stealth;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>Pure unit tests for the stealth building blocks that do not need a browser.</summary>
public class StealthUnitTests
{
    [Fact]
    public void World_names_are_random_hex_and_carry_no_library_identity()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        for (int i = 0; i < 500; i++)
        {
            string name = WorldNameFactory.Create();

            Assert.Equal(32, name.Length);
            Assert.All(name, c => Assert.True(char.IsAsciiHexDigitLower(c), $"Unexpected character '{c}'."));
            Assert.DoesNotContain("zerocdp", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("playwright", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cdp", name, StringComparison.OrdinalIgnoreCase);
            Assert.True(names.Add(name), "World names must not repeat.");
        }
    }

    [Fact]
    public void Parses_a_string_value_and_unescapes_it()
    {
        Assert.True(EvaluationResult.TryParse(
            """{"result":{"type":"string","value":"line\n\"quoted\""}}"""u8,
            out EvaluationResult result));

        Assert.False(result.Threw);
        Assert.Equal("string", result.Value.Type);
        Assert.True(result.Value.HasValue);
        Assert.Equal("line\n\"quoted\"", result.Value.StringValue);
    }

    [Fact]
    public void Parses_number_boolean_null_and_undefined_values()
    {
        Assert.True(EvaluationResult.TryParse("""{"result":{"type":"number","value":42}}"""u8, out EvaluationResult number));
        Assert.True(number.Value.TryGetInt64(out long wide));
        Assert.Equal(42, wide);

        Assert.True(EvaluationResult.TryParse("""{"result":{"type":"boolean","value":false}}"""u8, out EvaluationResult boolean));
        Assert.True(boolean.Value.TryGetBoolean(out bool flag));
        Assert.False(flag);

        Assert.True(EvaluationResult.TryParse("""{"result":{"type":"object","subtype":"null","value":null}}"""u8, out EvaluationResult nothing));
        Assert.True(nothing.Value.IsNull);

        Assert.True(EvaluationResult.TryParse("""{"result":{"type":"undefined"}}"""u8, out EvaluationResult undefined));
        Assert.True(undefined.Value.IsUndefined);
        Assert.False(undefined.Value.HasValue);
    }

    [Fact]
    public void Parses_exception_details_as_a_summary()
    {
        Assert.True(EvaluationResult.TryParse(
            """{"result":{"type":"object","subtype":"error"},"exceptionDetails":{"exceptionId":1,"text":"Uncaught","lineNumber":0,"columnNumber":6,"exception":{"type":"object","subtype":"error","className":"Error","description":"Error: boom\n    at <anonymous>:1:7"}}}"""u8,
            out EvaluationResult result));

        Assert.True(result.Threw);
        Assert.Equal("Error: boom", result.ExceptionSummary);
        Assert.Contains("at <anonymous>", result.ExceptionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_a_thrown_string()
    {
        Assert.True(EvaluationResult.TryParse(
            """{"result":{"type":"string","value":"nope"},"exceptionDetails":{"text":"Uncaught","exception":{"type":"string","value":"nope"}}}"""u8,
            out EvaluationResult result));

        Assert.True(result.Threw);
        Assert.Equal("nope", result.ExceptionSummary);
    }
}
