using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Stealth;

/// <summary>
/// Evaluates JavaScript in a CDP isolated world without enabling the Runtime domain.
/// </summary>
/// <remarks>
/// <para>
/// This is the core stealth primitive. <c>Runtime.enable</c> makes the page observable as being
/// debugged, so it is never sent: every expression is addressed to an explicit
/// <c>executionContextId</c> returned by <c>Page.createIsolatedWorld</c>. The isolated world has
/// its own globals but shares the DOM, which is exactly what automation needs.
/// </para>
/// <para>
/// The world is created lazily on first evaluation and invalidated when the main frame commits a
/// new document. A stale context is retried once rather than surfacing an error to the caller.
/// </para>
/// </remarks>
public sealed class IsolatedWorldRuntime
{
    private readonly CdpSession _session;
    private readonly Lock _gate = new();
    private string _frameId = string.Empty;
    private int _contextId;
    private int _generation;
    private Task<int>? _creating;

    internal IsolatedWorldRuntime(CdpSession session)
        : this(session, WorldNameFactory.Create())
    {
    }

    internal IsolatedWorldRuntime(CdpSession session, string worldName)
    {
        _session = session;
        WorldName = worldName;
    }

    /// <summary>Session that owns this world; a child session for out-of-process iframes.</summary>
    internal CdpSession Session => _session;

    /// <summary>Name handed to the browser. Random, so it never identifies this library.</summary>
    public string WorldName { get; }

    /// <summary>True while a context from the current document is available.</summary>
    public bool HasContext
    {
        get
        {
            lock (_gate)
            {
                return _contextId != 0;
            }
        }
    }

    /// <summary>Execution context id of the current document, or 0 when none has been created.</summary>
    public int ExecutionContextId
    {
        get
        {
            lock (_gate)
            {
                return _contextId;
            }
        }
    }

    /// <summary>Binds the runtime to the page's main frame. Called by <c>CdpPage.EnableAsync</c>.</summary>
    internal void SetFrameId(string frameId)
    {
        if (string.IsNullOrEmpty(frameId))
        {
            throw new ArgumentException("A frame id is required.", nameof(frameId));
        }

        lock (_gate)
        {
            if (!string.Equals(_frameId, frameId, StringComparison.Ordinal))
            {
                _frameId = frameId;
                _generation++;
                _contextId = 0;
            }
        }
    }

    /// <summary>Drops the cached context; the next evaluation creates a fresh world.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _contextId = 0;
        }
    }

    /// <summary>Evaluates <paramref name="expression"/> and returns the resulting value.</summary>
    /// <remarks>
    /// A JavaScript exception is reported through <see cref="EvaluationResult.Threw"/> rather than
    /// thrown as a <see cref="CdpCommandException"/>; only protocol failures throw.
    /// </remarks>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return await EvaluateCoreAsync(expression, retryStaleContext: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> in the page's main world. This is opt-in because,
    /// unlike isolated-world evaluation, page scripts can observe its globals and side effects.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateMainWorldAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return await _session
            .SendAsync<EvaluationResult, EvaluateParams>(
                "Runtime.evaluate"u8,
                EvaluateParams.MainWorld(expression),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> without serialising the value, so the caller can
    /// address the resulting JavaScript object (for example with <c>DOM.requestNode</c>).
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateObjectAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return await EvaluateObjectCoreAsync(expression, retryStaleContext: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Calls a function on an object returned by a previous evaluation.</summary>
    public async ValueTask<EvaluationResult> CallFunctionOnObjectAsync(
        string objectId,
        string functionDeclaration,
        string argumentsJson = "[]",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(functionDeclaration);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        return await CallFunctionOnObjectCoreAsync(
                objectId,
                functionDeclaration,
                argumentsJson,
                returnByValue: true,
                retryStaleContext: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a function on an object and keeps the result addressable instead of serialising it.
    /// Used by JSHandle and ElementHandle APIs.
    /// </summary>
    public async ValueTask<EvaluationResult> CallFunctionOnObjectByReferenceAsync(
        string objectId,
        string functionDeclaration,
        string argumentsJson = "[]",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(functionDeclaration);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        return await CallFunctionOnObjectCoreAsync(
                objectId,
                functionDeclaration,
                argumentsJson,
                returnByValue: false,
                retryStaleContext: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a function in the isolated world using <c>Runtime.callFunctionOn</c>.
    /// </summary>
    /// <param name="functionDeclaration">Function body, for example <c>function(a) { return a + 1; }</c>.</param>
    /// <param name="argumentsJson">Raw JSON array of CDP <c>CallArgument</c> values, for example <c>[{"value":41}]</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask<EvaluationResult> CallFunctionAsync(
        string functionDeclaration,
        string argumentsJson = "[]",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(functionDeclaration);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        return await CallFunctionCoreAsync(functionDeclaration, argumentsJson, retryStaleContext: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EvaluationResult> EvaluateCoreAsync(
        string expression,
        bool retryStaleContext,
        CancellationToken cancellationToken)
    {
        int contextId = await EnsureContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _session
                .SendAsync<EvaluationResult, EvaluateParams>(
                    "Runtime.evaluate"u8,
                    new EvaluateParams(expression, contextId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (retryStaleContext && IsStaleContext(ex))
        {
            Invalidate();
            return await EvaluateCoreAsync(expression, retryStaleContext: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<EvaluationResult> CallFunctionCoreAsync(
        string functionDeclaration,
        string argumentsJson,
        bool retryStaleContext,
        CancellationToken cancellationToken)
    {
        int contextId = await EnsureContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _session
                .SendAsync<EvaluationResult, CallFunctionOnParams>(
                    "Runtime.callFunctionOn"u8,
                    new CallFunctionOnParams(functionDeclaration, contextId, argumentsJson),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (retryStaleContext && IsStaleContext(ex))
        {
            Invalidate();
            return await CallFunctionCoreAsync(functionDeclaration, argumentsJson, retryStaleContext: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<EvaluationResult> EvaluateObjectCoreAsync(
        string expression,
        bool retryStaleContext,
        CancellationToken cancellationToken)
    {
        int contextId = await EnsureContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _session
                .SendAsync<EvaluationResult, EvaluateParams>(
                    "Runtime.evaluate"u8,
                    new EvaluateParams(expression, contextId, returnByValue: false),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (retryStaleContext && IsStaleContext(ex))
        {
            Invalidate();
            return await EvaluateObjectCoreAsync(expression, retryStaleContext: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<EvaluationResult> CallFunctionOnObjectCoreAsync(
        string objectId,
        string functionDeclaration,
        string argumentsJson,
        bool returnByValue,
        bool retryStaleContext,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _session
                .SendAsync<EvaluationResult, CallFunctionOnObjectParams>(
                    "Runtime.callFunctionOn"u8,
                    new CallFunctionOnObjectParams(
                        functionDeclaration,
                        objectId,
                        argumentsJson,
                        returnByValue),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (retryStaleContext && IsStaleContext(ex))
        {
            // The object belonged to a destroyed context. Retry once only helps when the caller
            // kept an id from an older document; there is nothing to recreate, so surface it.
            throw new CdpException(
                "The JavaScript object belonged to a destroyed execution context.",
                ex);
        }
    }

    private async ValueTask<int> EnsureContextAsync(CancellationToken cancellationToken)
    {
        Task<int> creation;
        lock (_gate)
        {
            if (_contextId != 0)
            {
                return _contextId;
            }

            if (string.IsNullOrEmpty(_frameId))
            {
                throw new CdpException("The isolated world has no frame to bind to yet.");
            }

            creation = _creating ??= CreateContextAsync(_frameId, _generation);
        }

        // The creation task is shared between concurrent callers, so the caller's token only
        // cancels this wait, not the creation itself.
        return await creation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CreateContextAsync(string frameId, int generation)
    {
        try
        {
            CreateIsolatedWorldResult result = await _session
                .SendAsync<CreateIsolatedWorldResult, CreateIsolatedWorldParams>(
                    "Page.createIsolatedWorld"u8,
                    new CreateIsolatedWorldParams(frameId, WorldName))
                .ConfigureAwait(false);

            lock (_gate)
            {
                // A navigation can land while the create command is in flight. Storing that
                // context would make the next evaluation run in the old document.
                if (generation == _generation)
                {
                    _contextId = result.ExecutionContextId;
                }
            }

            return result.ExecutionContextId;
        }
        finally
        {
            lock (_gate)
            {
                _creating = null;
            }
        }
    }

    private static bool IsStaleContext(CdpCommandException exception)
        => exception.Message.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Inspected target navigated or closed", StringComparison.OrdinalIgnoreCase);
}
