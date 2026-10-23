using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Captures the page as a self-contained MHTML document. Used by tracing for point-in-time DOM
    /// snapshots; unlike Playwright's snapshot format it does not include layout or ARIA metadata.
    /// </summary>
    internal async ValueTask<string> CaptureMhtmlAsync(CancellationToken cancellationToken = default)
    {
        CaptureSnapshotResult result = await Session
            .SendAsync<CaptureSnapshotResult, CaptureSnapshotParams>(
                "Page.captureSnapshot"u8,
                new CaptureSnapshotParams("mhtml"),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Data;
    }
}
