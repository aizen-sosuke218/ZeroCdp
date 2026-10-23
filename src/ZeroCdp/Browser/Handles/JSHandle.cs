using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// A handle to a JavaScript object in a page. Handles keep the object alive until disposed or the
/// document navigates away.
/// </summary>
public class JSHandle : IAsyncDisposable
{
    private readonly CdpPage _page;
    private readonly int _contextId;
    protected string? ObjectIdInternal { get; set; }

    private RemoteObject _value;
    private int _disposed;

    internal JSHandle(
        CdpPage page,
        string objectId,
        int contextId,
        string? description)
    {
        _page = page;
        ObjectIdInternal = objectId;
        _contextId = contextId;
        _value = new RemoteObject
        {
            Type = "object",
            Description = description ?? "JSHandle",
            ObjectId = objectId,
        };
    }

    internal JSHandle(CdpPage page, RemoteObject value, int contextId)
    {
        _page = page;
        _contextId = contextId;
        _value = value;
        ObjectIdInternal = value.ObjectId;
    }

    protected void SetObjectId(string objectId)
    {
        ObjectIdInternal = objectId;
        _value = new RemoteObject
        {
            Type = "object",
            Description = _value.Description,
            ObjectId = objectId,
        };
    }

    internal CdpPage Page => _page;

    internal int ContextId => _contextId;

    internal string? ObjectId => ObjectIdInternal;

    /// <summary>True after <see cref="DisposeAsync"/> has been called.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>A short description such as <c>HTMLButtonElement</c>.</summary>
    public string Description => _value.Description;

    /// <summary>A handle to the same object represented as an element, or null for non-elements.</summary>
    public async ValueTask<ElementHandle?> AsElementAsync(CancellationToken cancellationToken = default)
    {
        if (this is ElementHandle element)
        {
            return element;
        }

        if (ObjectIdInternal is null)
        {
            return null;
        }

        try
        {
            long nodeId = await _page.RequestNodeAsync(ObjectIdInternal, _contextId, cancellationToken)
                .ConfigureAwait(false);
            return new ElementHandle(_page, nodeId, _contextId, ObjectIdInternal);
        }
        catch (CdpException)
        {
            return null;
        }
    }

    /// <summary>Releases the remote object.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        GC.SuppressFinalize(this);
        if (ObjectIdInternal is null)
        {
            return;
        }

        try
        {
            await _page.ReleaseObjectAsync(ObjectIdInternal, _contextId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (CdpException)
        {
            // Navigation or page close already released the object.
        }
    }

    /// <summary>
    /// Evaluates a function with this handle as its argument. Function expressions such as
    /// <c>value => value.textContent</c> are supported; any other expression is evaluated directly.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            throw new CdpException("The handle refers to a primitive value and cannot be called.");
        }

        string function = BuildFunction(expression);
        EvaluationResult result = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                function,
                "[]",
                returnByReference: false,
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>Evaluates a function and keeps the result as a handle.</summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            throw new CdpException("The handle refers to a primitive value and cannot be called.");
        }

        string function = BuildFunction(expression);
        EvaluationResult result = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                function,
                "[]",
                returnByReference: true,
                cancellationToken)
            .ConfigureAwait(false);
        return FromResult(_page, result, _contextId);
    }

    /// <summary>Reads one property and returns a handle to it.</summary>
    public async ValueTask<JSHandle> GetPropertyAsync(
        string propertyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            throw new CdpException("The handle refers to a primitive value and has no properties.");
        }

        EvaluationResult result = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                "function(name) { return this[name]; }",
                JsText.StringArguments([propertyName]),
                returnByReference: true,
                cancellationToken)
            .ConfigureAwait(false);
        return FromResult(_page, result, _contextId);
    }

    /// <summary>Reads every enumerable own property as a handle.</summary>
    public async ValueTask<IReadOnlyDictionary<string, JSHandle>> GetPropertiesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            return new Dictionary<string, JSHandle>(StringComparer.Ordinal);
        }

        EvaluationResult namesResult = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                "function() { return JSON.stringify(Object.keys(this)); }",
                "[]",
                returnByReference: false,
                cancellationToken)
            .ConfigureAwait(false);
        string[] names = ReadStringArray(namesResult.Value.StringValue);
        Dictionary<string, JSHandle> properties = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            properties[name] = await GetPropertyAsync(name, cancellationToken).ConfigureAwait(false);
        }

        return properties;
    }

    /// <summary>The serialized value, or null for undefined.</summary>
    public async ValueTask<JsonElement?> JsonValueAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            return ParseValue(_value);
        }

        EvaluationResult result = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                "function() { return JSON.stringify(this); }",
                "[]",
                returnByReference: false,
                cancellationToken)
            .ConfigureAwait(false);
        string? json = result.Value.StringValue;
        if (json is null || json == "undefined")
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A string representation of the value.</summary>
    public async ValueTask<string> ToStringAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ObjectIdInternal is null)
        {
            return _value.StringValue ?? _value.UnserializableValue ?? _value.Description;
        }

        EvaluationResult result = await _page.CallOnObjectAsync(
                ObjectIdInternal,
                _contextId,
                "function() { return String(this); }",
                "[]",
                returnByReference: false,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Value.StringValue ?? result.Value.Description;
    }

    internal static JSHandle FromResult(CdpPage page, EvaluationResult result, int contextId)
    {
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "The JavaScript expression threw.");
        }

        return new JSHandle(page, result.Value, contextId);
    }

    private static string BuildFunction(string expression)
    {
        string trimmed = expression.TrimStart();
        bool functionLike = trimmed.Contains("=>", StringComparison.Ordinal)
            || trimmed.StartsWith("function", StringComparison.Ordinal)
            || trimmed.StartsWith("async", StringComparison.Ordinal);
        return functionLike
            ? "function() { return (" + expression + ")(this); }"
            : "function() { return (" + expression + "); }";
    }

    private static string[] ReadStringArray(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> values = new();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            values.Add(element.GetString() ?? string.Empty);
        }

        return values.ToArray();
    }

    private static JsonElement? ParseValue(RemoteObject value)
    {
        if (!value.HasValue || value.IsUndefined || value.IsNull)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(value.ValueJson);
        return document.RootElement.Clone();
    }
}
