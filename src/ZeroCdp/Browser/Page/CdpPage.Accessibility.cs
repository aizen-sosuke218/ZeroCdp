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
        CancellationToken cancellationToken)
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
        return root?.ToYaml() ?? string.Empty;
    }
}
