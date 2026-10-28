using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Evaluates an expression in one frame. The isolated world is the default; the main world is
    /// selected explicitly and is reached through the frame's own session, or through the iframe
    /// element's content window for same-process frames.
    /// </summary>
    internal async ValueTask<EvaluationResult> EvaluateInFrameAsync(
        string frameId,
        string expression,
        bool isolatedContext,
        bool returnByValue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        FrameContext context = GetFrameContext(frameId);
        if (isolatedContext)
        {
            return returnByValue
                ? await context.World.EvaluateAsync(expression, cancellationToken).ConfigureAwait(false)
                : await context.World.EvaluateObjectAsync(expression, cancellationToken).ConfigureAwait(false);
        }

        if (context.IsOopif || string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
        {
            // OOPIF sessions and the main frame have a main world addressed directly.
            return await context.Session
                .SendAsync<EvaluationResult, EvaluateParams>(
                    "Runtime.evaluate"u8,
                    EvaluateParams.MainWorld(expression, returnByValue),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Same-process iframe: resolving its owner element without an execution context id lands
        // in the parent frame's main world, and contentWindow.eval enters the child's main world.
        // The frame may itself live inside an OOPIF, so every command goes through its session.
        FrameOwnerResult owner = await context.Session
            .SendAsync<FrameOwnerResult, GetFrameOwnerParams>(
                "DOM.getFrameOwner"u8,
                new GetFrameOwnerParams(frameId),
                cancellationToken)
            .ConfigureAwait(false);

        // The owner element is not necessarily materialized in the front-end node table yet;
        // getFrameOwner then reports nodeId 0 and only the backend id is usable. Resolving by
        // backend id avoids DOM.getDocument, which is not otherwise requested here.
        ResolveNodeParams resolve = owner.NodeId != 0
            ? new ResolveNodeParams(owner.NodeId)
            : owner.BackendNodeId != 0
                ? new ResolveNodeParams(owner.BackendNodeId, useBackendNodeId: true)
                : throw new CdpException(
                    $"The owner element for frame '{frameId}' could not be resolved.");

        ResolveNodeResult resolved = await context.Session
            .SendAsync<ResolveNodeResult, ResolveNodeParams>(
                "DOM.resolveNode"u8,
                resolve,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            throw new CdpException("The iframe element could not be resolved to a JavaScript object.");
        }

        string arguments = "[{\"value\":" + JsText.Quote(expression) + "}]";
        const string function =
            "function(expr) { const win = this.contentWindow || (this.contentDocument && this.contentDocument.defaultView);" +
            " if (!win) throw new Error('The iframe has no content window.');" +
            " return win.eval(expr); }";

        EvaluationResult result = await context.Session
            .SendAsync<EvaluationResult, CallFunctionOnObjectParams>(
                "Runtime.callFunctionOn"u8,
                new CallFunctionOnObjectParams(
                    function,
                    resolved.ObjectId!,
                    arguments,
                    returnByValue),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await ReleaseObjectAsync(resolved.ObjectId!, context.Id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (CdpException)
        {
            // The iframe may have been detached; the result is still usable.
        }

        return result;
    }
}
