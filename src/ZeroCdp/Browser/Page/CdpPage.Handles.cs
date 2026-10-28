using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Evaluates an expression and returns a handle to the resulting JavaScript object. The
    /// Runtime domain is not enabled; the object lives in the isolated world.
    /// </summary>
    public ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
        => EvaluateHandleAsync(expression, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Evaluates an expression and returns a handle to the resulting JavaScript object. Handles
    /// live in the isolated world by default; pass <paramref name="isolatedContext"/> false to
    /// create one in the page's main world.
    /// </summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = isolatedContext
            ? await _isolated.EvaluateObjectAsync(expression, cancellationToken).ConfigureAwait(false)
            : await _isolated.EvaluateObjectMainWorldAsync(expression, cancellationToken).ConfigureAwait(false);
        return CreateValueHandle(result, contextId: 0);
    }

    internal JSHandle CreateHandle(EvaluationResult result, int contextId)
    {
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "The JavaScript expression threw.");
        }

        if (string.IsNullOrEmpty(result.Value.ObjectId))
        {
            throw new CdpException(
                "The expression returned a primitive value. EvaluateHandle requires an object or function.");
        }

        return new JSHandle(this, result.Value.ObjectId, contextId, result.Value.Description);
    }

    /// <summary>
    /// Wraps an evaluation result in a handle, allowing primitive values. The node id selects the
    /// frame context so later calls on an object handle route to the correct session.
    /// </summary>
    internal JSHandle CreateValueHandle(EvaluationResult result, long nodeId)
        => CreateValueHandle(result, ContextForNode(nodeId).Id);

    /// <summary>Wraps an evaluation result in a handle, allowing primitive values.</summary>
    internal JSHandle CreateValueHandle(EvaluationResult result, int contextId)
    {
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "The JavaScript expression threw.");
        }

        return string.IsNullOrEmpty(result.Value.ObjectId)
            ? JSHandle.FromResult(this, result, contextId)
            : new JSHandle(this, result.Value.ObjectId, contextId, result.Value.Description);
    }

    /// <summary>
    /// Evaluates an expression with an element as <c>this</c> and keeps the result as a handle.
    /// </summary>
    internal ValueTask<JSHandle> EvaluateHandleOnNodeAsync(
        long nodeId,
        string expression,
        CancellationToken cancellationToken)
        => EvaluateHandleOnNodeAsync(nodeId, expression, isolatedContext: true, cancellationToken);

    internal async ValueTask<JSHandle> EvaluateHandleOnNodeAsync(
        long nodeId,
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, isolatedContext, cancellationToken)
            .ConfigureAwait(false);
        string function = "function() { const fn = (" + expression + "); return typeof fn === 'function' ? fn(this) : fn; }";

        EvaluationResult result = await context.World
            .CallFunctionOnObjectByReferenceAsync(resolved.ObjectId!, function, "[]", cancellationToken)
            .ConfigureAwait(false);

        JSHandle handle = CreateHandle(result, context.Id);
        if (!string.Equals(handle.ObjectId, resolved.ObjectId, StringComparison.Ordinal))
        {
            try
            {
                await ReleaseObjectAsync(resolved.ObjectId!, context.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // Navigation or page close already released the object.
            }
        }

        return handle;
    }

    internal async ValueTask<EvaluationResult> CallOnObjectAsync(
        string objectId,
        int contextId,
        string functionDeclaration,
        string argumentsJson,
        bool returnByReference,
        CancellationToken cancellationToken)
    {
        FrameContext context = GetFrameContextById(contextId) ?? MainFrameContext;
        return returnByReference
            ? await context.World
                .CallFunctionOnObjectByReferenceAsync(
                    objectId,
                    functionDeclaration,
                    argumentsJson,
                    cancellationToken)
                .ConfigureAwait(false)
            : await context.World
                .CallFunctionOnObjectAsync(
                    objectId,
                    functionDeclaration,
                    argumentsJson,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    internal async ValueTask ReleaseObjectAsync(
        string objectId,
        int contextId,
        CancellationToken cancellationToken)
    {
        FrameContext context = GetFrameContextById(contextId) ?? MainFrameContext;
        await context.Session
            .SendAsync<Unit, ReleaseObjectParams>(
                "Runtime.releaseObject"u8,
                new ReleaseObjectParams(objectId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<long> RequestNodeAsync(
        string objectId,
        int contextId,
        CancellationToken cancellationToken)
    {
        FrameContext context = GetFrameContextById(contextId) ?? MainFrameContext;
        RequestNodeResult result = await context.Session
            .SendAsync<RequestNodeResult, RequestNodeParams>(
                "DOM.requestNode"u8,
                new RequestNodeParams(objectId),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.NodeId == 0)
        {
            throw new CdpException("The JavaScript object is not a DOM element.");
        }

        return EncodeNodeId(context, result.NodeId);
    }

    internal async ValueTask<ResolveNodeResult> ResolveNodeObjectAsync(
        long nodeId,
        CancellationToken cancellationToken)
        => await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);

    internal ElementHandle CreateElementHandle(long nodeId)
    {
        (int contextId, _) = DecodeNodeId(nodeId);
        return new ElementHandle(this, nodeId, contextId);
    }

    internal Frame? FindFrameById(string frameId)
    {
        foreach (Frame frame in Frames)
        {
            if (string.Equals(frame.FrameId, frameId, StringComparison.Ordinal))
            {
                return frame;
            }
        }

        return null;
    }

    internal async ValueTask<EvaluationResult> NodePropertyAsync(
        long nodeId,
        string functionDeclaration,
        CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return await context.World
            .CallFunctionOnObjectAsync(
                resolved.ObjectId!,
                functionDeclaration,
                "[]",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
