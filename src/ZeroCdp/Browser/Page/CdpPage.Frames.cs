using System.Text;
using ZeroCdp.Dom;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Selectors;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    internal async ValueTask<string> GetFrameIdAsync(long nodeId, CancellationToken cancellationToken)
    {
        DomNode? node = await GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(node?.FrameId))
        {
            throw new CdpException("The selector did not match an iframe or frame element.");
        }

        return node.FrameId;
    }

    internal async ValueTask<int> CountInFrameAsync(
        FrameScope frame,
        string selector,
        CancellationToken cancellationToken)
    {
        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        IsolatedWorldRuntime world = GetFrameRuntime(frameId);

        EvaluationResult result = await world
            .EvaluateAsync($"document.querySelectorAll({JsText.Quote(selector)}).length", cancellationToken)
            .ConfigureAwait(false);

        ThrowIfThrew(result, selector);
        return result.Value.TryGetInt64(out long count) ? (int)Math.Min(count, int.MaxValue) : 0;
    }

    internal async ValueTask<long> ResolveFirstInFrameAsync(
        FrameScope frame,
        string selector,
        CancellationToken cancellationToken)
    {
        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        FrameContext context = GetFrameContext(frameId);
        long nodeId = await QueryFrameNodeAsync(
                context.World,
                $"document.querySelector({JsText.Quote(selector)})",
                selector,
                cancellationToken)
            .ConfigureAwait(false);
        return EncodeNodeId(context, nodeId);
    }

    internal async ValueTask<long[]> ResolveInFrameAsync(
        FrameScope frame,
        string selector,
        CancellationToken cancellationToken)
    {
        int count = await CountInFrameAsync(frame, selector, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return [];
        }

        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        FrameContext context = GetFrameContext(frameId);
        List<long> nodes = new(count);

        for (int i = 0; i < count; i++)
        {
            string expression = $"document.querySelectorAll({JsText.Quote(selector)})[{i}]";
            long nodeId = await QueryFrameNodeAsync(context.World, expression, selector, cancellationToken)
                .ConfigureAwait(false);
            if (nodeId != 0)
            {
                nodes.Add(EncodeNodeId(context, nodeId));
            }
        }

        return nodes.ToArray();
    }

    internal async ValueTask<ElementHandle?> FrameElementHandleAsync(
        string frameId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
        {
            return null;
        }

        FrameContext self = GetFrameContext(frameId);
        FrameContext parent = self.Id == 0 ? MainFrameContext : self.Parent ?? MainFrameContext;

        FrameOwnerResult owner = await parent.Session
            .SendAsync<FrameOwnerResult, GetFrameOwnerParams>(
                "DOM.getFrameOwner"u8,
                new GetFrameOwnerParams(frameId),
                cancellationToken)
            .ConfigureAwait(false);

        long nodeId = owner.NodeId;
        if (nodeId == 0)
        {
            NodeIdsResult pushed = await parent.Session
                .SendAsync<NodeIdsResult, PushNodesByBackendIdsParams>(
                    "DOM.pushNodesByBackendIdsToFrontend"u8,
                    new PushNodesByBackendIdsParams([owner.BackendNodeId]),
                    cancellationToken)
                .ConfigureAwait(false);

            if (pushed.NodeIds.Length == 0)
            {
                throw new CdpException($"The owner element for frame '{frameId}' could not be resolved.");
            }

            nodeId = pushed.NodeIds[0];
        }

        return CreateElementHandle(EncodeNodeId(parent, nodeId));
    }

    internal IsolatedWorldRuntime GetFrameRuntime(string frameId)
    {
        if (string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
        {
            return _isolated;
        }

        lock (_oopifGate)
        {
            if (_frameContextsByFrame.TryGetValue(frameId, out FrameContext? context))
            {
                return context.World;
            }

            if (_childSessionsByFrame.TryGetValue(frameId, out CdpSession? child))
            {
                return CreateFrameContextLocked(frameId, child).World;
            }
        }

        return GetSameProcessFrameRuntime(frameId);
    }

    private IsolatedWorldRuntime GetSameProcessFrameRuntime(string frameId)
    {
        lock (_frameWorldsGate)
        {
            if (!_frameWorlds.TryGetValue(frameId, out IsolatedWorldRuntime? runtime))
            {
                runtime = new IsolatedWorldRuntime(Session, WorldNameFactory.Create());
                runtime.SetFrameId(frameId);
                _frameWorlds[frameId] = runtime;
            }

            return runtime;
        }
    }

    private static async ValueTask<long> QueryFrameNodeAsync(
        IsolatedWorldRuntime world,
        string expression,
        string selector,
        CancellationToken cancellationToken)
    {
        EvaluationResult result = await world.EvaluateObjectAsync(expression, cancellationToken).ConfigureAwait(false);
        ThrowIfThrew(result, selector);

        string? objectId = result.Value.ObjectId;
        if (string.IsNullOrEmpty(objectId))
        {
            return 0;
        }

        RequestNodeResult node = await world.Session
            .SendAsync<RequestNodeResult, RequestNodeParams>(
                "DOM.requestNode"u8,
                new RequestNodeParams(objectId),
                cancellationToken)
            .ConfigureAwait(false);

        return node.NodeId;
    }
}
