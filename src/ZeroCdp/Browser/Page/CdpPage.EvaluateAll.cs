using System.Text;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    internal ValueTask<EvaluationResult> EvaluateOnNodesAsync(
        long[] nodeIds,
        string functionDeclaration,
        CancellationToken cancellationToken)
        => EvaluateOnNodesAsync(nodeIds, functionDeclaration, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Calls a function with an array containing every supplied element. The function runs in the
    /// isolated world of the frame that owns the nodes by default, or in that frame's main world
    /// when <paramref name="isolatedContext"/> is false.
    /// </summary>
    internal async ValueTask<EvaluationResult> EvaluateOnNodesAsync(
        long[] nodeIds,
        string functionDeclaration,
        bool isolatedContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(functionDeclaration);
        if (nodeIds.Length == 0)
        {
            throw new CdpException("No elements matched the locator.");
        }

        (int contextId, _) = DecodeNodeId(nodeIds[0]);
        for (int i = 1; i < nodeIds.Length; i++)
        {
            (int otherContextId, _) = DecodeNodeId(nodeIds[i]);
            if (otherContextId != contextId)
            {
                throw new CdpException("EvaluateAll requires all matched elements to belong to one frame.");
            }
        }

        FrameContext context = ContextForNode(nodeIds[0]);
        int executionContextId = isolatedContext
            ? await context.World.EnsureContextIdAsync(cancellationToken).ConfigureAwait(false)
            : 0;

        List<string> objectIds;
        try
        {
            objectIds = await ResolveObjectIdsAsync(context, nodeIds, executionContextId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (isolatedContext && IsolatedWorldRuntime.IsStaleContext(ex))
        {
            context.World.Invalidate();
            int retryContextId = await context.World
                .EnsureContextIdAsync(cancellationToken)
                .ConfigureAwait(false);
            objectIds = await ResolveObjectIdsAsync(context, nodeIds, retryContextId, cancellationToken)
                .ConfigureAwait(false);
        }

        StringBuilder arguments = new(objectIds.Count * 32 + 2);
        arguments.Append('[');
        for (int i = 0; i < objectIds.Count; i++)
        {
            if (i > 0)
            {
                arguments.Append(',');
            }

            arguments.Append("{\"objectId\":").Append(JsText.Quote(objectIds[i])).Append('}');
        }

        arguments.Append(']');

        string wrapper = "function() { return (" + functionDeclaration
            + ")(Array.prototype.slice.call(arguments)); }";
        return await context.Session
            .SendAsync<EvaluationResult, CallFunctionOnObjectParams>(
                "Runtime.callFunctionOn"u8,
                new CallFunctionOnObjectParams(wrapper, objectIds[0], arguments.ToString()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<List<string>> ResolveObjectIdsAsync(
        FrameContext context,
        long[] nodeIds,
        int executionContextId,
        CancellationToken cancellationToken)
    {
        List<string> objectIds = new(nodeIds.Length);
        foreach (long nodeId in nodeIds)
        {
            ResolveNodeResult resolved = await context.Session
                .SendAsync<ResolveNodeResult, ResolveNodeParams>(
                    "DOM.resolveNode"u8,
                    new ResolveNodeParams(RealNodeId(nodeId), executionContextId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(resolved.ObjectId))
            {
                throw new CdpException("DOM.resolveNode did not return an object id.");
            }

            objectIds.Add(resolved.ObjectId);
        }

        return objectIds;
    }
}
