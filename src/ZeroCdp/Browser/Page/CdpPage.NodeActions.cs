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
    internal async ValueTask SetFileInputFilesAsync(
        long nodeId,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        await context.Session
            .SendAsync<Unit, SetFileInputFilesParams>(
                "DOM.setFileInputFiles"u8,
                new SetFileInputFilesParams(RealNodeId(nodeId), files),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask ClearFileInputAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            throw new CdpException("The file input could not be resolved to a JavaScript object.");
        }

        const string Function = """
            function() {
              this.value = '';
              this.dispatchEvent(new Event('input', { bubbles: true }));
              this.dispatchEvent(new Event('change', { bubbles: true }));
            }
            """;

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId, Function, "[]", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Clearing the file input threw in the page.");
        }
    }

    /// <summary>
    /// Reject a click when the point would land on another element. On by default; it costs two
    /// extra protocol calls per click and never injects script into the page.
    /// </summary>
    public bool CheckHitTarget { get; set; } = true;

    /// <summary>
    /// Require the element's box to be unchanged across two animation frames before clicking, so an
    /// element that is still moving cannot be clicked at a stale coordinate.
    /// </summary>
    public bool WaitForStableBox { get; set; } = true;

    /// <summary>
    /// The point to click for <paramref name="nodeId"/>, or null when the element is moving or
    /// covered. Returning null lets the actionability loop retry instead of clicking the wrong spot.
    /// </summary>
    internal async ValueTask<(double X, double Y)?> TryGetActionPointAsync(
        long nodeId,
        CancellationToken cancellationToken)
    {
        (double X, double Y) first;
        try
        {
            first = await GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            return null;
        }

        if (WaitForStableBox)
        {
            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            (double X, double Y) second;
            try
            {
                second = await GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                return null;
            }

            if (Math.Abs(first.X - second.X) > 0.5 || Math.Abs(first.Y - second.Y) > 0.5)
            {
                return null;
            }
        }

        if (CheckHitTarget && !await IsClickPointClearAsync(nodeId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return first;
    }

    /// <summary>
    /// True when a hit test at the element's centre lands on the element or one of its descendants.
    /// </summary>
    /// <remarks>
    /// Closed shadow roots are the one deliberate approximation: <c>elementFromPoint</c> reports the
    /// host, so a hit on the host (or inside it) is accepted when the target lives in a closed tree.
    /// </remarks>
    internal async ValueTask<bool> IsClickPointClearAsync(long nodeId, CancellationToken cancellationToken)
    {
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            return false;
        }

        const string Function = """
            function() {
              const rect = this.getBoundingClientRect();
              if (!(rect.width > 0 && rect.height > 0)) return false;
              const x = rect.left + rect.width / 2;
              const y = rect.top + rect.height / 2;
              const hit = this.ownerDocument.elementFromPoint(x, y);
              if (!hit) return false;
              if (hit === this || this.contains(hit)) return true;
              let root = this.getRootNode();
              while (root && root.host) {
                if (root.host === hit || root.host.contains(hit)) return true;
                root = root.host.getRootNode();
              }
              return false;
            }
            """;

        FrameContext context = ContextForNode(nodeId);
        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId, Function, "[]", cancellationToken)
            .ConfigureAwait(false);

        return !result.Threw && result.Value.TryGetBoolean(out bool clear) && clear;
    }

    internal async ValueTask ClickNodeAtAsync(
        double x,
        double y,
        string button,
        int clickCount,
        TimeSpan? delay,
        int modifiers,
        CancellationToken cancellationToken)
        => await _input
            .ClickAsync(x, y, button, clickCount, delay, modifiers, cancellationToken)
            .ConfigureAwait(false);

    internal async ValueTask<(double X, double Y)> GetNodeCenterAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        await ScrollToNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);

        ContentQuads quads = await context.Session
            .SendAsync<ContentQuads, GetContentQuadsParams>(
                "DOM.getContentQuads"u8,
                new GetContentQuadsParams(RealNodeId(nodeId)),
                cancellationToken)
            .ConfigureAwait(false);

        if (quads.Quads.Length == 0)
        {
            throw new CdpException("The element has no visible box, so it cannot be interacted with.");
        }

        double[] quad = quads.Quads[0];
        double x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4;
        double y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4;
        FrameTransform transform = await GetFrameTransformAsync(context, cancellationToken).ConfigureAwait(false);
        return transform.Map(x, y);
    }

    private static void ThrowIfThrew(EvaluationResult result, string selector)
    {
        if (result.Threw)
        {
            throw new CdpException(
                $"Evaluating a frame query for '{selector}' threw: {result.ExceptionSummary}");
        }
    }

    private static string[] ReadStringArray(ReadOnlySpan<byte> json)
    {
        Json.JsonCursor cursor = new(json);
        if (!cursor.TryEnterArray())
        {
            return [];
        }

        List<string> values = new(4);
        while (cursor.TryNextArrayElement())
        {
            if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
            {
                if (!escaped)
                {
                    values.Add(System.Text.Encoding.UTF8.GetString(raw));
                }
                else
                {
                    byte[] buffer = new byte[raw.Length];
                    if (Json.JsonUtf8.TryUnescape(raw, buffer, out int written))
                    {
                        values.Add(System.Text.Encoding.UTF8.GetString(buffer, 0, written));
                    }
                }
            }
            else if (!cursor.SkipValue())
            {
                break;
            }
        }

        return values.ToArray();
    }

}
