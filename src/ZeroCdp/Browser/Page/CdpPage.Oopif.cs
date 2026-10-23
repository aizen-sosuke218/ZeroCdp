using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

/// <summary>
/// Routing context for one frame. Context 0 is the main page or a same-process iframe addressed
/// through the parent session; contexts above zero are out-of-process iframes with their own
/// session, isolated world and synthetic node-id range.
/// </summary>
internal sealed class FrameContext
{
    public FrameContext(
        int id,
        string frameId,
        CdpSession session,
        IsolatedWorldRuntime world,
        FrameContext? parent = null)
    {
        Id = id;
        FrameId = frameId;
        Session = session;
        World = world;
        Parent = parent;
    }

    public int Id { get; }

    public string FrameId { get; }

    public CdpSession Session { get; }

    public IsolatedWorldRuntime World { get; }

    /// <summary>Frame that owns this frame's iframe element; null for the main frame.</summary>
    public FrameContext? Parent { get; }

    public bool IsOopif => Id != 0;
}

public sealed partial class CdpPage
{
    private const int FrameContextShift = 40;
    private const long FrameNodeIdMask = (1L << FrameContextShift) - 1;

    private readonly Lock _oopifGate = new();
    private readonly Dictionary<string, CdpSession> _childSessionsByTarget = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CdpSession> _childSessionsByFrame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameContext> _frameContextsByFrame = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FrameContext> _frameContextsById = new();
    private readonly Dictionary<string, FrameContext> _frameContextsBySession = new(StringComparer.Ordinal);
    private int _nextFrameContextId = 1;

    /// <summary>
    /// Child target sessions auto-attached by this page, including out-of-process iframes. The
    /// browser's flattened auto-attach reports them on the parent session; this is the page-local
    /// view used for frame routing and diagnostics.
    /// </summary>
    public IReadOnlyList<CdpSession> ChildSessions
    {
        get
        {
            lock (_oopifGate)
            {
                return _childSessionsByTarget.Values.Distinct().ToArray();
            }
        }
    }

    internal void OnChildSessionAttached(CdpSession session)
    {
        if (IsWorkerTarget(session.TargetType))
        {
            RegisterWorker(session);
            return;
        }

        if (!session.TargetType.Equals("iframe", StringComparison.Ordinal))
        {
            return;
        }

        lock (_oopifGate)
        {
            _childSessionsByTarget[session.TargetId] = session;
            // Chromium reports targetId == frameId for iframe targets. Registering that mapping
            // immediately avoids a race where a locator resolves the frame before getFrameTree
            // has returned.
            _childSessionsByFrame[session.TargetId] = session;
        }

        session.EventSink = new ChildFrameEventSink(this, session);
        _ = RegisterChildFrameAsync(session);
    }

    internal void OnChildSessionDetached(CdpSession session)
    {
        if (IsWorkerTarget(session.TargetType))
        {
            RemoveWorker(session);
            return;
        }

        List<FrameContext> removed = new();
        lock (_oopifGate)
        {
            _childSessionsByTarget.Remove(session.TargetId);
            _childSessionsByFrame.Remove(session.TargetId);

            foreach ((string frameId, CdpSession child) in _childSessionsByFrame.ToArray())
            {
                if (ReferenceEquals(child, session))
                {
                    _childSessionsByFrame.Remove(frameId);
                }
            }

            foreach ((string frameId, FrameContext context) in _frameContextsByFrame.ToArray())
            {
                if (ReferenceEquals(context.Session, session))
                {
                    _frameContextsByFrame.Remove(frameId);
                    _frameContextsById.Remove(context.Id);
                    _frameContextsBySession.Remove(context.Session.SessionId);
                    removed.Add(context);
                }
            }
        }

        foreach (FrameContext context in removed)
        {
            context.World.Invalidate();
        }
    }

    private async Task RegisterChildFrameAsync(CdpSession session)
    {
        try
        {
            // Auto-attach is not inherited by child targets: an iframe nested inside this OOPIF
            // needs its own page-session auto-attach to expose the next target in the chain.
            await session
                .SendAsync<Unit, SetAutoAttachParams>(
                    "Target.setAutoAttach"u8,
                    new SetAutoAttachParams(
                        autoAttach: true,
                        waitForDebuggerOnStart: true,
                        flatten: true))
                .ConfigureAwait(false);

            FrameTreeResult tree = await session
                .SendAsync<FrameTreeResult>("Page.getFrameTree"u8)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(tree.MainFrameId))
            {
                return;
            }

            lock (_oopifGate)
            {
                _childSessionsByFrame[tree.MainFrameId] = session;
            }

            RegisterFrameTree(tree, GetParentFrameIdForSession(session));
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // The iframe may have been removed before it could report its frame tree. The
            // target-id mapping registered above is enough for the common Chromium case.
        }
    }

    /// <summary>Frame that owns a child session, used when the child's own frame tree has no parent id.</summary>
    internal string? GetParentFrameIdForSession(CdpSession session)
    {
        lock (_oopifGate)
        {
            if (string.Equals(session.ParentSessionId, Session.SessionId, StringComparison.Ordinal))
            {
                return _mainFrameId;
            }

            if (!string.IsNullOrEmpty(session.ParentSessionId)
                && _frameContextsBySession.TryGetValue(session.ParentSessionId, out FrameContext? parent))
            {
                return parent.FrameId;
            }
        }

        return _mainFrameId;
    }

    /// <summary>Returns the routing context for a frame id, creating one for an OOPIF on demand.</summary>
    internal FrameContext GetFrameContext(string frameId)
    {
        ArgumentException.ThrowIfNullOrEmpty(frameId);

        if (string.Equals(frameId, _mainFrameId, StringComparison.Ordinal))
        {
            return new FrameContext(0, frameId, Session, _isolated);
        }

        lock (_oopifGate)
        {
            if (_frameContextsByFrame.TryGetValue(frameId, out FrameContext? existing))
            {
                return existing;
            }

            if (_childSessionsByFrame.TryGetValue(frameId, out CdpSession? child))
            {
                return CreateFrameContextLocked(frameId, child);
            }
        }

        // A same-process iframe is addressed through the parent session. It still needs its own
        // isolated world, but its node ids and coordinates live in the parent viewport.
        return new FrameContext(0, frameId, Session, GetSameProcessFrameRuntime(frameId));
    }

    private FrameContext CreateFrameContextLocked(string frameId, CdpSession session)
    {
        int id = _nextFrameContextId++;
        IsolatedWorldRuntime world = new(session, WorldNameFactory.Create());
        world.SetFrameId(frameId);

        FrameContext? parent = null;
        if (!string.IsNullOrEmpty(session.ParentSessionId))
        {
            _frameContextsBySession.TryGetValue(session.ParentSessionId, out parent);
        }

        parent ??= MainFrameContext;
        FrameContext context = new(id, frameId, session, world, parent);
        _frameContextsByFrame[frameId] = context;
        _frameContextsById[id] = context;
        _frameContextsBySession[session.SessionId] = context;
        return context;
    }

    internal FrameContext MainFrameContext
        => new(0, _mainFrameId ?? string.Empty, Session, _isolated);

    internal FrameContext? GetFrameContextById(int contextId)
    {
        if (contextId == 0)
        {
            return MainFrameContext;
        }

        lock (_oopifGate)
        {
            return _frameContextsById.GetValueOrDefault(contextId);
        }
    }

    /// <summary>Encodes a child-session node id so the page can route later operations.</summary>
    internal static long EncodeNodeId(FrameContext context, long nodeId)
        => context.Id == 0 ? nodeId : ((long)context.Id << FrameContextShift) | (nodeId & FrameNodeIdMask);

    internal async ValueTask<long[]> PushBackendIdsAsync(
        int contextId,
        long[] backendIds,
        CancellationToken cancellationToken)
    {
        FrameContext context = GetFrameContextById(contextId)
            ?? throw new CdpException(
                $"The DOM node belongs to an iframe target that is no longer attached (context {contextId}).");

        NodeIdsResult result = await context.Session
            .SendAsync<NodeIdsResult, PushNodesByBackendIdsParams>(
                "DOM.pushNodesByBackendIdsToFrontend"u8,
                new PushNodesByBackendIdsParams(backendIds),
                cancellationToken)
            .ConfigureAwait(false);

        return EncodeNodeIds(context, result.NodeIds);
    }

    internal static long[] EncodeNodeIds(FrameContext context, long[] nodeIds)
    {
        if (context.Id == 0 || nodeIds.Length == 0)
        {
            return nodeIds;
        }

        long[] encoded = new long[nodeIds.Length];
        for (int i = 0; i < nodeIds.Length; i++)
        {
            encoded[i] = EncodeNodeId(context, nodeIds[i]);
        }

        return encoded;
    }

    internal static (int ContextId, long NodeId) DecodeNodeId(long nodeId)
        => nodeId > FrameNodeIdMask
            ? ((int)(nodeId >> FrameContextShift), nodeId & FrameNodeIdMask)
            : (0, nodeId);

    internal FrameContext ContextForNode(long nodeId)
    {
        (int contextId, _) = DecodeNodeId(nodeId);
        FrameContext? context = GetFrameContextById(contextId);
        return context ?? throw new CdpException(
            $"The DOM node belongs to an iframe target that is no longer attached (context {contextId}).");
    }

    internal static long RealNodeId(long nodeId) => DecodeNodeId(nodeId).NodeId;

    /// <summary>Groups synthetic node ids by the frame context that owns them.</summary>
    internal List<(FrameContext Context, long[] NodeIds)> GroupNodesByContext(long[] nodeIds)
    {
        Dictionary<int, List<long>> groups = new();
        foreach (long nodeId in nodeIds)
        {
            (int contextId, long realNodeId) = DecodeNodeId(nodeId);
            if (!groups.TryGetValue(contextId, out List<long>? list))
            {
                list = new List<long>();
                groups[contextId] = list;
            }

            list.Add(realNodeId);
        }

        List<(FrameContext Context, long[] NodeIds)> result = new(groups.Count);
        foreach ((int contextId, List<long> ids) in groups)
        {
            FrameContext? context = GetFrameContextById(contextId);
            if (context is null)
            {
                throw new CdpException(
                    $"The DOM node belongs to an iframe target that is no longer attached (context {contextId}).");
            }

            result.Add((context, ids.ToArray()));
        }

        return result;
    }

    internal static async ValueTask<DomNode?> GetSnapshotRootForContextAsync(
        FrameContext context,
        CancellationToken cancellationToken)
    {
        DocumentTree tree = await context.Session
            .SendAsync<DocumentTree, GetDocumentParams>(
                "DOM.getDocument"u8,
                new GetDocumentParams(
                    depth: context.Session.Connection.Options.MaxDomDepth,
                    pierce: true),
                cancellationToken)
            .ConfigureAwait(false);

        return DomTreeParser.Parse(tree.RootJson.Span);
    }

    internal async ValueTask<FrameTransform> GetFrameTransformAsync(
        FrameContext context,
        CancellationToken cancellationToken)
    {
        if (context.Id == 0)
        {
            return FrameTransform.Identity;
        }

        FrameContext parent = context.Parent ?? MainFrameContext;
        FrameOwnerResult owner = await parent.Session
            .SendAsync<FrameOwnerResult, GetFrameOwnerParams>(
                "DOM.getFrameOwner"u8,
                new GetFrameOwnerParams(context.FrameId),
                cancellationToken)
            .ConfigureAwait(false);

        BoxModelResult box = await parent.Session
            .SendAsync<BoxModelResult, GetBoxModelParams>(
                "DOM.getBoxModel"u8,
                new GetBoxModelParams(owner.BackendNodeId, useBackendNodeId: true),
                cancellationToken)
            .ConfigureAwait(false);

        LayoutMetricsResult metrics = await context.Session
            .SendAsync<LayoutMetricsResult>("Page.getLayoutMetrics"u8, cancellationToken)
            .ConfigureAwait(false);

        double viewportWidth = metrics.ViewportWidth > 0 ? metrics.ViewportWidth : metrics.Width;
        double viewportHeight = metrics.ViewportHeight > 0 ? metrics.ViewportHeight : metrics.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            throw new CdpException("The out-of-process iframe reported an empty layout viewport.");
        }

        FrameTransform local = FrameTransform.FromContentQuad(box.Content, viewportWidth, viewportHeight);
        if (parent.IsOopif)
        {
            FrameTransform parentTransform = await GetFrameTransformAsync(parent, cancellationToken)
                .ConfigureAwait(false);
            return parentTransform.Compose(local);
        }

        return local;
    }

    /// <summary>Affine map from an OOPIF's local CSS pixels to the parent page viewport.</summary>
    internal readonly struct FrameTransform
    {
        private readonly double _originX;
        private readonly double _originY;
        private readonly double _uX;
        private readonly double _uY;
        private readonly double _vX;
        private readonly double _vY;

        private FrameTransform(
            double originX,
            double originY,
            double uX,
            double uY,
            double vX,
            double vY)
        {
            _originX = originX;
            _originY = originY;
            _uX = uX;
            _uY = uY;
            _vX = vX;
            _vY = vY;
        }

        public static FrameTransform Identity { get; } = new(0, 0, 1, 0, 0, 1);

        public static FrameTransform FromContentQuad(
            ReadOnlySpan<double> quad,
            double childWidth,
            double childHeight)
        {
            if (quad.Length < 8 || childWidth <= 0 || childHeight <= 0)
            {
                throw new CdpException("The iframe owner has no usable content box.");
            }

            double uX = (quad[2] - quad[0]) / childWidth;
            double uY = (quad[3] - quad[1]) / childWidth;
            double vX = (quad[6] - quad[0]) / childHeight;
            double vY = (quad[7] - quad[1]) / childHeight;
            return new FrameTransform(quad[0], quad[1], uX, uY, vX, vY);
        }

        public (double X, double Y) Map(double x, double y)
            => (_originX + (x * _uX) + (y * _vX), _originY + (x * _uY) + (y * _vY));

        /// <summary>Returns a transform that applies <paramref name="inner"/> and then this one.</summary>
        public FrameTransform Compose(FrameTransform inner)
            => new(
                _originX + (inner._originX * _uX) + (inner._originY * _vX),
                _originY + (inner._originX * _uY) + (inner._originY * _vY),
                (inner._uX * _uX) + (inner._uY * _vX),
                (inner._uX * _uY) + (inner._uY * _vY),
                (inner._vX * _uX) + (inner._vY * _vX),
                (inner._vX * _uY) + (inner._vY * _vY));

        public double[] MapQuad(ReadOnlySpan<double> quad)
        {
            if (quad.Length < 8)
            {
                return [];
            }

            double[] mapped = new double[8];
            for (int i = 0; i + 1 < 8; i += 2)
            {
                (mapped[i], mapped[i + 1]) = Map(quad[i], quad[i + 1]);
            }

            return mapped;
        }
    }

    private static bool IsWorkerTarget(string targetType)
        => targetType.Equals("worker", StringComparison.Ordinal)
            || targetType.Equals("shared_worker", StringComparison.Ordinal)
            || targetType.Equals("service_worker", StringComparison.Ordinal);

    private void RegisterWorker(CdpSession session)
    {
        Worker worker = new(this, session);
        Action<Worker>? handlers;
        lock (_pageLifecycleGate)
        {
            _workers.Add(worker);
            handlers = _workerHandlers;
        }

        handlers?.Invoke(worker);
    }

    private void RemoveWorker(CdpSession session)
    {
        lock (_pageLifecycleGate)
        {
            for (int i = _workers.Count - 1; i >= 0; i--)
            {
                if (_workers[i].TargetId.Equals(session.TargetId, StringComparison.Ordinal))
                {
                    _workers[i].MarkClosed();
                    _workers.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
