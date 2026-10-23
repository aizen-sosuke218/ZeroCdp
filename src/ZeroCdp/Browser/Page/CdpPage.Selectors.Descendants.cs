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
    internal async ValueTask<long[]> ResolveDescendantsByXPathAsync(
        long[] parentNodeIds,
        ZeroCdp.Selectors.XPathSelector selector,
        CancellationToken cancellationToken)
    {
        if (parentNodeIds.Length == 0)
        {
            return [];
        }

        HashSet<long> seen = new();
        List<long> matches = new();
        foreach ((FrameContext context, long[] ids) in GroupNodesByContext(parentNodeIds))
        {
            HashSet<long> parentBackends = new();
            foreach (long parentNodeId in ids)
            {
                DomNode? info = await GetNodeInfoAsync(parentNodeId, cancellationToken).ConfigureAwait(false);
                if (info is { BackendNodeId: > 0 })
                {
                    parentBackends.Add(info.BackendNodeId);
                }
            }

            if (parentBackends.Count == 0)
            {
                continue;
            }

            Dom.DomNode? root = await GetSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            List<DomNode> scopes = new();
            Stack<DomNode> pending = new();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DomNode node = pending.Pop();
                if (node.IsElement && parentBackends.Contains(node.BackendNodeId))
                {
                    scopes.Add(node);
                }

                foreach (DomNode child in node.TraversalChildren())
                {
                    pending.Push(child);
                }
            }

            foreach (DomNode scope in scopes)
            {
                foreach (long nodeId in selector.ResolveUnder(scope))
                {
                    long encoded = EncodeNodeId(context, nodeId);
                    if (seen.Add(encoded))
                    {
                        matches.Add(encoded);
                    }
                }
            }
        }

        return matches.ToArray();
    }

    internal async ValueTask<long[]> ResolveInFrameByMatcherAsync(
        FrameScope frame,
        ZeroCdp.Selectors.CompiledSelector[] selectors,
        CancellationToken cancellationToken)
    {
        FrameContext context = await ResolveFrameContextAsync(frame, cancellationToken).ConfigureAwait(false);
        Dom.DomNode? root = await GetFrameSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
        return root is null
            ? []
            : EncodeNodeIds(
                context,
                TraverseMatches(root, node => ZeroCdp.Selectors.SelectorMatcher.MatchesAny(selectors, node)));
    }

    internal async ValueTask<long[]> ResolveBySpecInFrameAsync(
        FrameScope frame,
        LocatorSpec spec,
        CancellationToken cancellationToken)
    {
        FrameContext context = await ResolveFrameContextAsync(frame, cancellationToken).ConfigureAwait(false);
        Dom.DomNode? root = await GetFrameSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        SemanticContext semantic = new(root);
        return EncodeNodeIds(
            context,
            TraverseMatches(root, node => LocatorSpecMatcher.Matches(spec, node, semantic)));
    }

    /// <summary>Resolves a CSS descendant query inside each parent node.</summary>
    internal async ValueTask<long[]> ResolveDescendantsByQueryAsync(
        long[] parentNodeIds,
        string selector,
        CancellationToken cancellationToken)
    {
        if (parentNodeIds.Length == 0)
        {
            return [];
        }

        List<long> nodes = new();
        HashSet<long> seen = new();
        foreach ((FrameContext context, long[] ids) in GroupNodesByContext(parentNodeIds))
        {
            foreach (long parentNodeId in ids)
            {
                NodeIdsResult result = await context.Session
                    .SendAsync<NodeIdsResult, QuerySelectorAllParams>(
                        "DOM.querySelectorAll"u8,
                        new QuerySelectorAllParams(parentNodeId, selector),
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (long nodeId in result.NodeIds)
                {
                    long encoded = EncodeNodeId(context, nodeId);
                    if (seen.Add(encoded))
                    {
                        nodes.Add(encoded);
                    }
                }
            }
        }

        return nodes.ToArray();
    }

    /// <summary>Matches a CSS descendant query against a pierced snapshot, scoped to parent nodes.</summary>
    internal async ValueTask<long[]> ResolveDescendantsByMatcherAsync(
        long[] parentNodeIds,
        ZeroCdp.Selectors.CompiledSelector[] selectors,
        CancellationToken cancellationToken)
        => await ResolveDescendantsByMatcherCoreAsync(
                parentNodeIds,
                selectors,
                null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Matches a semantic descendant query against a pierced snapshot, scoped to parent nodes.</summary>
    internal async ValueTask<long[]> ResolveDescendantsByMatcherAsync(
        long[] parentNodeIds,
        LocatorSpec spec,
        CancellationToken cancellationToken)
        => await ResolveDescendantsByMatcherCoreAsync(
                parentNodeIds,
                null,
                spec,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<long[]> ResolveDescendantsByMatcherCoreAsync(
        long[] parentNodeIds,
        ZeroCdp.Selectors.CompiledSelector[]? selectors,
        LocatorSpec? spec,
        CancellationToken cancellationToken)
    {
        if (parentNodeIds.Length == 0)
        {
            return [];
        }

        List<long> matches = new();
        HashSet<long>? seen = parentNodeIds.Length > 1 ? new HashSet<long>() : null;
        foreach ((FrameContext context, long[] ids) in GroupNodesByContext(parentNodeIds))
        {
            long[] raw = await ResolveDescendantsByMatcherInContextAsync(
                    context,
                    ids,
                    selectors,
                    spec,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (long nodeId in raw)
            {
                long encoded = EncodeNodeId(context, nodeId);
                if (seen is null || seen.Add(encoded))
                {
                    matches.Add(encoded);
                }
            }
        }

        return matches.ToArray();
    }

    private async ValueTask<long[]> ResolveDescendantsByMatcherInContextAsync(
        FrameContext context,
        long[] parentNodeIds,
        ZeroCdp.Selectors.CompiledSelector[]? selectors,
        LocatorSpec? spec,
        CancellationToken cancellationToken)
    {
        // Backend node ids survive DOM.getDocument, unlike the node ids used for DOM commands.
        HashSet<long> parentBackends = new();
        foreach (long parentNodeId in parentNodeIds)
        {
            DomNode? info = await GetNodeInfoAsync(parentNodeId, cancellationToken).ConfigureAwait(false);
            if (info is { BackendNodeId: > 0 })
            {
                parentBackends.Add(info.BackendNodeId);
            }
        }

        if (parentBackends.Count == 0)
        {
            return [];
        }

        DomNode? root = await GetSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        HashSet<DomNode> parentDocuments = new();
        CollectParentDocuments(root, parentBackends, parentDocuments);
        if (parentDocuments.Count == 0)
        {
            return [];
        }

        SemanticContext? semantic = spec is null ? null : new SemanticContext(root);
        List<long> matches = new();
        HashSet<long>? seen = parentNodeIds.Length > 1 ? new HashSet<long>() : null;
        Stack<DomNode> pending = new();
        pending.Push(root);
        List<DomNode> children = new();

        while (pending.Count > 0)
        {
            DomNode node = pending.Pop();

            if (node.IsElement
                && node.NodeId != 0
                && IsScopedDescendant(node, parentBackends, parentDocuments))
            {
                bool matched = spec is not null
                    ? LocatorSpecMatcher.Matches(spec, node, semantic!)
                    : ZeroCdp.Selectors.SelectorMatcher.MatchesAny(selectors!, node);

                if (matched && (seen is null || seen.Add(node.NodeId)))
                {
                    matches.Add(node.NodeId);
                }
            }

            children.Clear();
            foreach (DomNode child in node.TraversalChildren())
            {
                children.Add(child);
            }

            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        return matches.ToArray();
    }

    private static void CollectParentDocuments(
        DomNode root,
        HashSet<long> parentBackends,
        HashSet<DomNode> documents)
    {
        Stack<DomNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DomNode node = pending.Pop();
            if (node.IsElement && parentBackends.Contains(node.BackendNodeId))
            {
                DomNode document = node;
                while (document.Parent is not null)
                {
                    document = document.Parent;
                }

                documents.Add(document);
            }

            foreach (DomNode child in node.TraversalChildren())
            {
                pending.Push(child);
            }
        }
    }

    private static bool IsScopedDescendant(
        DomNode node,
        HashSet<long> parentBackends,
        HashSet<DomNode> parentDocuments)
    {
        DomNode document = node;
        while (document.Parent is not null)
        {
            document = document.Parent;
        }

        if (!parentDocuments.Contains(document))
        {
            return false;
        }

        for (DomNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (parentBackends.Contains(ancestor.BackendNodeId))
            {
                return true;
            }
        }

        return false;
    }

}
