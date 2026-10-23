using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Page.createIsolatedWorld</c>.</summary>
/// <remarks>
/// An isolated world has its own JavaScript globals while sharing the DOM with the main world.
/// Creating one does not inject anything into the page, and it does not require
/// <c>Runtime.enable</c>.
/// </remarks>
public readonly struct CreateIsolatedWorldParams : ICdpCommandParams
{
    private readonly string _frameId;
    private readonly string _worldName;
    private readonly bool _grantUniversalAccess;

    public CreateIsolatedWorldParams(string frameId, string worldName, bool grantUniversalAccess = false)
    {
        _frameId = frameId;
        _worldName = worldName;
        _grantUniversalAccess = grantUniversalAccess;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("frameId"u8);
        writer.StringValue(_frameId);
        writer.Name("worldName"u8);
        writer.StringValue(_worldName);

        // The protocol really does spell the field this way.
        writer.Name("grantUniveralAccess"u8);
        writer.BoolValue(_grantUniversalAccess);
    }
}

/// <summary>Result of <c>Page.createIsolatedWorld</c>.</summary>
public readonly struct CreateIsolatedWorldResult : ICdpResult<CreateIsolatedWorldResult>
{
    public int ExecutionContextId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out CreateIsolatedWorldResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("executionContextId"u8) && cursor.TryGetInt32(out int contextId))
            {
                value = new CreateIsolatedWorldResult { ExecutionContextId = contextId };
                return true;
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Parameters of <c>Runtime.evaluate</c>, scoped to an execution context.</summary>
/// <summary>Parameters of <c>Runtime.addBinding</c>.</summary>
public readonly struct AddBindingParams : ICdpCommandParams
{
    private readonly string _name;

    public AddBindingParams(string name) => _name = name;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("name"u8);
        writer.StringValue(_name);
    }
}

public readonly struct EvaluateParams : ICdpCommandParams
{
    private readonly string _expression;
    private readonly int _contextId;
    private readonly bool _returnByValue;
    private readonly bool _awaitPromise;
    private readonly bool _includeContextId;

    public EvaluateParams(string expression, int contextId, bool returnByValue = true, bool awaitPromise = true)
    {
        _expression = expression;
        _contextId = contextId;
        _returnByValue = returnByValue;
        _awaitPromise = awaitPromise;
        _includeContextId = true;
    }

    private EvaluateParams(
        string expression,
        int contextId,
        bool returnByValue,
        bool awaitPromise,
        bool includeContextId)
    {
        _expression = expression;
        _contextId = contextId;
        _returnByValue = returnByValue;
        _awaitPromise = awaitPromise;
        _includeContextId = includeContextId;
    }

    /// <summary>Evaluates in the main world, without creating or addressing an isolated context.</summary>
    public static EvaluateParams MainWorld(
        string expression,
        bool returnByValue = true,
        bool awaitPromise = true)
        => new(expression, contextId: 0, returnByValue, awaitPromise, includeContextId: false);

    public void Write(ref CommandWriter writer)
    {
        writer.Name("expression"u8);
        writer.StringValue(_expression);

        if (_includeContextId)
        {
            writer.Name("contextId"u8);
            writer.Int32Value(_contextId);
        }

        writer.Name("returnByValue"u8);
        writer.BoolValue(_returnByValue);
        writer.Name("awaitPromise"u8);
        writer.BoolValue(_awaitPromise);
    }
}

/// <summary>Parameters of <c>Runtime.callFunctionOn</c>, scoped to an execution context.</summary>
public readonly struct CallFunctionOnParams : ICdpCommandParams
{
    private readonly string _functionDeclaration;
    private readonly int _contextId;
    private readonly string _argumentsJson;
    private readonly bool _returnByValue;
    private readonly bool _awaitPromise;

    /// <param name="argumentsJson">
    /// Raw JSON array of CDP <c>CallArgument</c> objects, for example
    /// <c>[{"value":"hello"},{"value":42}]</c>. Callers own serialization so this struct stays
    /// allocation free on the write path.
    /// </param>
    public CallFunctionOnParams(
        string functionDeclaration,
        int contextId,
        string argumentsJson = "[]",
        bool returnByValue = true,
        bool awaitPromise = true)
    {
        _functionDeclaration = functionDeclaration;
        _contextId = contextId;
        _argumentsJson = argumentsJson;
        _returnByValue = returnByValue;
        _awaitPromise = awaitPromise;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("functionDeclaration"u8);
        writer.StringValue(_functionDeclaration);
        writer.Name("arguments"u8);
        writer.Value(Encoding.UTF8.GetBytes(_argumentsJson));
        writer.Name("executionContextId"u8);
        writer.Int32Value(_contextId);
        writer.Name("returnByValue"u8);
        writer.BoolValue(_returnByValue);
        writer.Name("awaitPromise"u8);
        writer.BoolValue(_awaitPromise);
    }
}

/// <summary>
/// Parameters of <c>Runtime.callFunctionOn</c> addressed to a remote object instead of an
/// execution context.
/// </summary>
public readonly struct CallFunctionOnObjectParams : ICdpCommandParams
{
    private readonly string _functionDeclaration;
    private readonly string _objectId;
    private readonly string _argumentsJson;
    private readonly bool _returnByValue;
    private readonly bool _awaitPromise;

    public CallFunctionOnObjectParams(
        string functionDeclaration,
        string objectId,
        string argumentsJson = "[]",
        bool returnByValue = true,
        bool awaitPromise = true)
    {
        _functionDeclaration = functionDeclaration;
        _objectId = objectId;
        _argumentsJson = argumentsJson;
        _returnByValue = returnByValue;
        _awaitPromise = awaitPromise;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("functionDeclaration"u8);
        writer.StringValue(_functionDeclaration);
        writer.Name("objectId"u8);
        writer.StringValue(_objectId);
        writer.Name("arguments"u8);
        writer.Value(Encoding.UTF8.GetBytes(_argumentsJson));
        writer.Name("returnByValue"u8);
        writer.BoolValue(_returnByValue);
        writer.Name("awaitPromise"u8);
        writer.BoolValue(_awaitPromise);
    }
}

/// <summary>
/// A JavaScript value as reported by <c>Runtime.evaluate</c> or <c>Runtime.callFunctionOn</c>.
/// </summary>
/// <remarks>
/// <see cref="ValueJson"/> is the raw JSON of the <c>value</c> member, so callers who need a
/// number, object or array can parse it without this struct imposing a shape. Strings are
/// already unescaped into <see cref="StringValue"/>.
/// </remarks>
public readonly struct RemoteObject
{
    public string Type { get; init; }

    public string Subtype { get; init; }

    public string ClassName { get; init; }

    public string Description { get; init; }

    public string? ObjectId { get; init; }

    public string? UnserializableValue { get; init; }

    /// <summary>Raw JSON of <c>value</c>, or empty when the value was not returned by value.</summary>
    public ReadOnlyMemory<byte> ValueJson { get; init; }

    /// <summary>True when the protocol included a <c>value</c> member, including <c>null</c>.</summary>
    public bool HasValue { get; init; }

    /// <summary>Unescaped text when <see cref="Type"/> is <c>string</c>.</summary>
    public string? StringValue { get; init; }

    public bool IsUndefined => Type.Length == 0 || Type == "undefined";

    public bool IsNull => Type == "object" && Subtype == "null";

    public bool TryGetBoolean(out bool value)
    {
        value = false;
        if (!HasValue)
        {
            return false;
        }

        JsonCursor cursor = new(ValueJson.Span);
        return cursor.TryGetBoolean(out value);
    }

    public bool TryGetInt64(out long value)
    {
        value = 0;
        if (!HasValue)
        {
            return false;
        }

        JsonCursor cursor = new(ValueJson.Span);
        return cursor.TryGetInt64(out value);
    }

    public bool TryGetDouble(out double value)
    {
        value = 0;
        if (!HasValue)
        {
            return false;
        }

        JsonCursor cursor = new(ValueJson.Span);
        return cursor.TryGetDouble(out value);
    }

    internal static bool TryRead(ref JsonCursor cursor, out RemoteObject value)
    {
        value = default;
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string type = string.Empty;
        string subtype = string.Empty;
        string className = string.Empty;
        string description = string.Empty;
        string? objectId = null;
        string? unserializableValue = null;
        ReadOnlyMemory<byte> valueJson = default;
        bool hasValue = false;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("type"u8))
            {
                type = ReadString(ref cursor) ?? type;
            }
            else if (name.SequenceEqual("subtype"u8))
            {
                subtype = ReadString(ref cursor) ?? subtype;
            }
            else if (name.SequenceEqual("className"u8))
            {
                className = ReadString(ref cursor) ?? className;
            }
            else if (name.SequenceEqual("description"u8))
            {
                description = ReadString(ref cursor) ?? description;
            }
            else if (name.SequenceEqual("objectId"u8))
            {
                objectId = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("unserializableValue"u8))
            {
                unserializableValue = ReadString(ref cursor);
            }
            else if (name.SequenceEqual("value"u8))
            {
                int start = cursor.Position;
                if (!cursor.SkipValue())
                {
                    return false;
                }

                valueJson = cursor.Utf8[start..cursor.Position].ToArray();
                hasValue = true;
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new RemoteObject
        {
            Type = type,
            Subtype = subtype,
            ClassName = className,
            Description = description,
            ObjectId = objectId,
            UnserializableValue = unserializableValue,
            ValueJson = valueJson,
            HasValue = hasValue,
            StringValue = type == "string" && hasValue ? DecodeString(valueJson.Span) : null,
        };
        return true;
    }

    internal static string? ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return null;
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : null;
    }

    private static string? DecodeString(ReadOnlySpan<byte> rawJson)
    {
        JsonCursor cursor = new(rawJson);
        return ReadString(ref cursor);
    }
}

/// <summary>Parameters of <c>Runtime.releaseObject</c>.</summary>
public readonly struct ReleaseObjectParams : ICdpCommandParams
{
    private readonly string _objectId;

    public ReleaseObjectParams(string objectId) => _objectId = objectId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("objectId"u8);
        writer.StringValue(_objectId);
    }
}

/// <summary>Result of <c>Runtime.evaluate</c> and <c>Runtime.callFunctionOn</c>.</summary>
public readonly struct EvaluationResult : ICdpResult<EvaluationResult>
{
    /// <summary>The value the expression produced.</summary>
    public RemoteObject Value { get; init; }

    /// <summary>True when the expression threw; <see cref="ExceptionSummary"/> then explains why.</summary>
    public bool Threw { get; init; }

    /// <summary>Human readable exception text, for example <c>Uncaught Error: boom</c>.</summary>
    public string? ExceptionText { get; init; }

    /// <summary>
    /// First line of the exception text. Kept as a separate property so callers do not have to
    /// split the full text (which may contain a stack trace).
    /// </summary>
    public string? ExceptionSummary
    {
        get
        {
            if (ExceptionText is null)
            {
                return null;
            }

            int newline = ExceptionText.IndexOf('\n');
            return newline < 0 ? ExceptionText : ExceptionText[..newline];
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out EvaluationResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        RemoteObject remote = default;
        bool sawResult = false;
        bool threw = false;
        string? exceptionText = null;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("result"u8))
            {
                if (!RemoteObject.TryRead(ref cursor, out remote))
                {
                    return false;
                }

                sawResult = true;
            }
            else if (name.SequenceEqual("exceptionDetails"u8))
            {
                if (!TryReadException(ref cursor, out exceptionText))
                {
                    return false;
                }

                threw = true;
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (!sawResult)
        {
            return false;
        }

        value = new EvaluationResult
        {
            Value = remote,
            Threw = threw,
            ExceptionText = exceptionText,
        };
        return true;
    }

    private static bool TryReadException(ref JsonCursor cursor, out string? text)
    {
        text = null;
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string? detailsText = null;
        string? description = null;
        string? stringValue = null;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("text"u8))
            {
                detailsText = RemoteObject.ReadString(ref cursor);
            }
            else if (name.SequenceEqual("exception"u8))
            {
                if (!RemoteObject.TryRead(ref cursor, out RemoteObject exception))
                {
                    return false;
                }

                description = exception.Description;
                stringValue = exception.StringValue;
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        text = FirstNonEmpty(description, stringValue, detailsText);
        return true;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? candidate in values)
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
