using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Evaluates an expression and returns a handle to the resulting JavaScript object. The
    /// Runtime domain is not enabled; the object lives in the isolated world.
    /// </summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await _isolated.EvaluateObjectAsync(expression, cancellationToken)
            .ConfigureAwait(false);
        return CreateHandle(result, contextId: 0);
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
