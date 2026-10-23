using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    public async ValueTask<long[]> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_orSource is not null)
        {
            return await ResolveOrAsync(_orSource, _orWith!, cancellationToken).ConfigureAwait(false);
        }

        if (_filterSource is not null)
        {
            return await ApplyFilterAsync(
                    await _filterSource.ResolveAsync(cancellationToken).ConfigureAwait(false),
                    _filter!,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (_andSource is not null)
        {
            long[] left = await _andSource.ResolveAsync(cancellationToken).ConfigureAwait(false);
            HashSet<long> leftBackends = await GetBackendsAsync(left, cancellationToken).ConfigureAwait(false);
            long[] right = await _andWith!.ResolveAsync(cancellationToken).ConfigureAwait(false);
            return await KeepByBackendsAsync(right, leftBackends, cancellationToken).ConfigureAwait(false);
        }

        if (_parent is not null)
        {
            return await ResolveUnderParentAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_plan.Spec is { } spec)
        {
            return _frame is not null
                ? await _page.ResolveBySpecInFrameAsync(_frame.Value, spec, cancellationToken).ConfigureAwait(false)
                : await _page.ResolveBySpecAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        if (_plan.XPath is { } xpath)
        {
            return _frame is not null
                ? await _page.ResolveInFrameByXPathAsync(_frame.Value, xpath, cancellationToken).ConfigureAwait(false)
                : await _page.ResolveByXPathAsync(xpath, cancellationToken).ConfigureAwait(false);
        }

        if (_plan.ManagedOnly)
        {
            return _frame is not null
                ? await _page.ResolveInFrameByMatcherAsync(_frame.Value, _plan.Compiled!, cancellationToken).ConfigureAwait(false)
                : await _page.ResolveByMatcherAsync(_plan.Compiled!, cancellationToken).ConfigureAwait(false);
        }

        if (_frame is not null)
        {
            if (PierceShadowDom)
            {
                return await _page
                    .ResolveInFrameByMatcherAsync(_frame.Value, _plan.Compiled!, cancellationToken)
                    .ConfigureAwait(false);
            }

            long[] frameNodes = await _page
                .ResolveInFrameAsync(_frame.Value, _plan.Css!, cancellationToken)
                .ConfigureAwait(false);

            // The same closed-shadow fallback used at page scope applies inside frames.
            return frameNodes.Length == 0
                ? await _page
                    .ResolveInFrameByMatcherAsync(_frame.Value, GetFallbackSelectors()!, cancellationToken)
                    .ConfigureAwait(false)
                : frameNodes;
        }

        if (PierceShadowDom)
        {
            return await _page.ResolveByMatcherAsync(_plan.Compiled!, cancellationToken).ConfigureAwait(false);
        }

        long[] nodes = await _page.ResolveByQueryAsync(_plan.Css!, cancellationToken).ConfigureAwait(false);

        // Patchright-style closed-shadow support: the normal browser query stays the fast path,
        // and a pierced snapshot is used only when that path finds nothing. Open shadow roots are
        // already part of querySelectorAll; this adds closed roots without injecting JavaScript.
        return nodes.Length == 0
            ? await ResolveClosedShadowFallbackAsync(cancellationToken).ConfigureAwait(false)
            : nodes;
    }

    /// <summary>
    /// Intersects two node-id sets by <c>backendNodeId</c>.
    /// </summary>
    /// <remarks>
    /// Frontend node ids are only valid until the next <c>DOM.getDocument</c>, and resolving the
    /// second locator performs one. Backend ids survive that call, so the intersection is computed
    /// on them and the result uses the fresher (right-hand) ids that are safe to act on.
    /// </remarks>
    private async ValueTask<long[]> ResolveOrAsync(
        Locator left,
        Locator right,
        CancellationToken cancellationToken)
    {
        List<(long NodeId, long BackendId)> leftEntries = await GetBackendEntriesAsync(
                await left.ResolveAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        List<(long NodeId, long BackendId)> rightEntries = await GetBackendEntriesAsync(
                await right.ResolveAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        return await PushUnionAsync(leftEntries, rightEntries, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long[]> ResolveOrWithinAsync(
        Locator left,
        Locator right,
        long scopeNodeId,
        CancellationToken cancellationToken)
    {
        List<(long NodeId, long BackendId)> leftEntries = await GetBackendEntriesAsync(
                await left.ResolveWithinAsync(scopeNodeId, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        List<(long NodeId, long BackendId)> rightEntries = await GetBackendEntriesAsync(
                await right.ResolveWithinAsync(scopeNodeId, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        return await PushUnionAsync(leftEntries, rightEntries, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<List<(long NodeId, long BackendId)>> GetBackendEntriesAsync(
        long[] nodeIds,
        CancellationToken cancellationToken)
    {
        List<(long NodeId, long BackendId)> entries = new(nodeIds.Length);
        foreach (long nodeId in nodeIds)
        {
            DomNode? info = await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (info is { BackendNodeId: > 0 })
            {
                entries.Add((nodeId, info.BackendNodeId));
            }
        }

        return entries;
    }

    private async ValueTask<long[]> PushUnionAsync(
        List<(long NodeId, long BackendId)> left,
        List<(long NodeId, long BackendId)> right,
        CancellationToken cancellationToken)
    {
        Dictionary<int, List<long>> groups = new();
        List<int> order = new();
        HashSet<(int ContextId, long BackendId)> seen = new();

        AddEntries(left);
        AddEntries(right);

        List<long> result = new(left.Count + right.Count);
        foreach (int contextId in order)
        {
            long[] nodes = await _page
                .PushBackendIdsAsync(contextId, groups[contextId].ToArray(), cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(nodes);
        }

        return result.ToArray();

        void AddEntries(List<(long NodeId, long BackendId)> entries)
        {
            foreach ((long nodeId, long backendId) in entries)
            {
                (int contextId, _) = CdpPage.DecodeNodeId(nodeId);
                if (!seen.Add((contextId, backendId)))
                {
                    continue;
                }

                if (!groups.TryGetValue(contextId, out List<long>? ids))
                {
                    ids = new List<long>();
                    groups[contextId] = ids;
                    order.Add(contextId);
                }

                ids.Add(backendId);
            }
        }
    }

    private async ValueTask<HashSet<long>> GetBackendsAsync(
        long[] nodeIds,
        CancellationToken cancellationToken)
    {
        HashSet<long> backends = new();
        foreach (long nodeId in nodeIds)
        {
            DomNode? info = await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (info is { BackendNodeId: > 0 })
            {
                backends.Add(info.BackendNodeId);
            }
        }

        return backends;
    }

    private async ValueTask<long[]> KeepByBackendsAsync(
        long[] nodes,
        HashSet<long> backends,
        CancellationToken cancellationToken)
    {
        if (nodes.Length == 0 || backends.Count == 0)
        {
            return [];
        }

        List<long> kept = new(nodes.Length);
        foreach (long nodeId in nodes)
        {
            DomNode? info = await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (info is { BackendNodeId: > 0 } && backends.Contains(info.BackendNodeId))
            {
                kept.Add(nodeId);
            }
        }

        return kept.ToArray();
    }

    private async ValueTask<long[]> ApplyFilterAsync(
        long[] nodes,
        LocatorFilterOptions options,
        CancellationToken cancellationToken)
    {
        if (nodes.Length == 0)
        {
            return nodes;
        }

        List<long> kept = new(nodes.Length);
        foreach (long node in nodes)
        {
            if (options.Visible && !await _page.HasBoxAsync(node, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (options.HasText is { } hasText)
            {
                string text = await _page.NodeTextAsync(node, cancellationToken).ConfigureAwait(false);
                if (!TextNormalizer.Normalize(text).Contains(TextNormalizer.Normalize(hasText), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (options.HasNotText is { } hasNotText)
            {
                string text = await _page.NodeTextAsync(node, cancellationToken).ConfigureAwait(false);
                if (TextNormalizer.Normalize(text).Contains(TextNormalizer.Normalize(hasNotText), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (options.Has is { } has
                && await has.ResolveWithinAsync(node, cancellationToken).ConfigureAwait(false) is { Length: 0 })
            {
                continue;
            }

            if (options.HasNot is { } hasNot
                && await hasNot.ResolveWithinAsync(node, cancellationToken).ConfigureAwait(false) is { Length: > 0 })
            {
                continue;
            }

            kept.Add(node);
        }

        return kept.ToArray();
    }

    /// <summary>Resolves this locator inside the subtree of one element.</summary>
    internal async ValueTask<long[]> ResolveWithinAsync(long scopeNodeId, CancellationToken cancellationToken)
    {
        if (_orSource is not null)
        {
            return await ResolveOrWithinAsync(_orSource, _orWith!, scopeNodeId, cancellationToken).ConfigureAwait(false);
        }

        if (_andSource is not null)
        {
            long[] left = await _andSource.ResolveWithinAsync(scopeNodeId, cancellationToken).ConfigureAwait(false);
            HashSet<long> leftBackends = await GetBackendsAsync(left, cancellationToken).ConfigureAwait(false);
            long[] right = await _andWith!.ResolveWithinAsync(scopeNodeId, cancellationToken).ConfigureAwait(false);
            return await KeepByBackendsAsync(right, leftBackends, cancellationToken).ConfigureAwait(false);
        }

        if (_filterSource is not null)
        {
            return await ApplyFilterAsync(
                    await _filterSource.ResolveWithinAsync(scopeNodeId, cancellationToken).ConfigureAwait(false),
                    _filter!,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (_plan.Spec is { } spec)
        {
            return await _page.ResolveDescendantsByMatcherAsync([scopeNodeId], spec, cancellationToken).ConfigureAwait(false);
        }

        if (_plan.XPath is { } xpath)
        {
            return await _page.ResolveDescendantsByXPathAsync([scopeNodeId], xpath, cancellationToken).ConfigureAwait(false);
        }

        if (PierceShadowDom || _plan.ManagedOnly)
        {
            return await _page.ResolveDescendantsByMatcherAsync([scopeNodeId], _plan.Compiled!, cancellationToken).ConfigureAwait(false);
        }

        long[] nodes = await _page.ResolveDescendantsByQueryAsync([scopeNodeId], _plan.Css!, cancellationToken).ConfigureAwait(false);
        if (nodes.Length > 0)
        {
            return nodes;
        }

        CompiledSelector[]? fallback = GetFallbackSelectors();
        return fallback is null
            ? []
            : await _page.ResolveDescendantsByMatcherAsync([scopeNodeId], fallback, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a locator restricted to the first match.</summary>

    private async ValueTask<DomNode?> TryGetFirstInfoAsync(CancellationToken cancellationToken)
    {
        long nodeId;
        try
        {
            nodeId = await FindFirstAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ElementNotFoundException)
        {
            return null;
        }

        return nodeId == 0
            ? null
            : await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until the locator reaches <paramref name="state"/>.</summary>
    /// <exception cref="CdpTimeoutException">The state was not reached in time.</exception>

    private async ValueTask<long> FindFirstAsync(CancellationToken cancellationToken)
    {
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        return nodes.Length == 0 ? 0 : nodes[0];
    }

    private async ValueTask<long[]> ResolveUnderParentAsync(CancellationToken cancellationToken)
    {
        // The parent locator may carry its own nth/first/last selection. Chaining must preserve it,
        // so the parent's matches are indexed before they become descendant scopes.
        long[] parents = _parent!.ApplyIndex(
            await _parent.ResolveAsync(cancellationToken).ConfigureAwait(false));
        if (parents.Length == 0)
        {
            return [];
        }

        if (_plan.Spec is { } spec)
        {
            return await _page
                .ResolveDescendantsByMatcherAsync(parents, spec, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_plan.XPath is { } xpath)
        {
            return await _page
                .ResolveDescendantsByXPathAsync(parents, xpath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (PierceShadowDom || _plan.ManagedOnly)
        {
            return await _page
                .ResolveDescendantsByMatcherAsync(parents, _plan.Compiled!, cancellationToken)
                .ConfigureAwait(false);
        }

        long[] nodes = await _page
            .ResolveDescendantsByQueryAsync(parents, _plan.Css!, cancellationToken)
            .ConfigureAwait(false);
        return nodes.Length == 0
            ? await ResolveClosedShadowFallbackUnderParentAsync(parents, cancellationToken).ConfigureAwait(false)
            : nodes;
    }

    private async ValueTask<long[]> ResolveClosedShadowFallbackAsync(CancellationToken cancellationToken)
    {
        CompiledSelector[]? selectors = GetFallbackSelectors();
        if (selectors is null || !ShouldRunFallback())
        {
            return [];
        }

        return await _page.ResolveByMatcherAsync(selectors, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long[]> ResolveClosedShadowFallbackUnderParentAsync(
        long[] parentNodeIds,
        CancellationToken cancellationToken)
    {
        CompiledSelector[]? selectors = GetFallbackSelectors();
        if (selectors is null || !ShouldRunFallback())
        {
            return [];
        }

        return await _page
            .ResolveDescendantsByMatcherAsync(parentNodeIds, selectors, cancellationToken)
            .ConfigureAwait(false);
    }

    private CompiledSelector[]? GetFallbackSelectors()
    {
        lock (_fallbackGate)
        {
            if (_fallbackParseState == 2)
            {
                return null;
            }

            if (_fallbackParseState == 1)
            {
                return _fallbackSelectors;
            }

            try
            {
                CompiledSelector[] selectors = _plan.Compiled ?? CssSelectorParser.ParseList(_plan.Css!);
                _fallbackSelectors = selectors;
                _fallbackParseState = 1;
                return selectors;
            }
            catch (SelectorSyntaxException)
            {
                _fallbackParseState = 2;
                return null;
            }
        }
    }

    private bool ShouldRunFallback()
    {
        // The snapshot is much more expensive than querySelectorAll. While an action is polling,
        // retry at a human-scale interval instead of on every 50 ms tick.
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextFallbackTick))
        {
            return false;
        }

        Volatile.Write(ref _nextFallbackTick, now + 200);
        return true;
    }
}
