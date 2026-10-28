using ZeroCdp.Dom;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Builds the Playwright-style YAML aria snapshot for one node. The node is described first
    /// so the AX command can target its backend node id, including nodes inside OOPIFs.
    /// </summary>
    internal async ValueTask<string> GetAriaSnapshotAsync(
        long nodeId,
        bool interestingOnly,
        CancellationToken cancellationToken,
        int? depth = null)
    {
        FrameContext context = ContextForNode(nodeId);
        DomNode? info = await GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (info is null || info.BackendNodeId == 0)
        {
            throw new CdpException("The node could not be resolved for an accessibility snapshot.");
        }

        AXTreeResult tree = await context.Session
            .SendAsync<AXTreeResult>("Accessibility.getFullAXTree"u8, cancellationToken)
            .ConfigureAwait(false);

        AccessibilityNode? root = AccessibilityTreeParser.Parse(
            tree.NodesJson,
            interestingOnly,
            info.BackendNodeId);
        return root?.ToYaml(depth) ?? string.Empty;
    }

    /// <summary>
    /// Returns a Playwright-style YAML aria snapshot of the whole page. Depth limits the number
    /// of nested levels; null includes the full tree.
    /// </summary>
    internal async ValueTask<string> GetPageAriaSnapshotAsync(
        bool interestingOnly,
        int? depth,
        CancellationToken cancellationToken)
    {
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        AXTreeResult tree = await Session
            .SendAsync<AXTreeResult>("Accessibility.getFullAXTree"u8, cancellationToken)
            .ConfigureAwait(false);

        AccessibilityNode? root = AccessibilityTreeParser.Parse(tree.NodesJson, interestingOnly);
        return root?.ToYaml(depth) ?? string.Empty;
    }
}
