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
    private int _overlayEnabled;

    internal async ValueTask ScrollToNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        long realNodeId = RealNodeId(nodeId);
        await context.Session
            .SendAsync<Unit, ScrollIntoViewParams>(
                "DOM.scrollIntoViewIfNeeded"u8,
                new ScrollIntoViewParams(realNodeId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<bool> HasBoxAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ContentQuads quads = await context.Session
            .SendAsync<ContentQuads, GetContentQuadsParams>(
                "DOM.getContentQuads"u8,
                new GetContentQuadsParams(RealNodeId(nodeId)),
                cancellationToken)
            .ConfigureAwait(false);

        return quads.Quads.Length > 0;
    }

    internal async ValueTask<BoundingBox?> GetNodeBoundingBoxAsync(
        long nodeId,
        CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ContentQuads quads = await context.Session
            .SendAsync<ContentQuads, GetContentQuadsParams>(
                "DOM.getContentQuads"u8,
                new GetContentQuadsParams(RealNodeId(nodeId)),
                cancellationToken)
            .ConfigureAwait(false);

        if (quads.Quads.Length == 0)
        {
            return null;
        }

        FrameTransform transform = await GetFrameTransformAsync(context, cancellationToken).ConfigureAwait(false);
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;

        foreach (double[] quad in quads.Quads)
        {
            double[] mapped = transform.MapQuad(quad);
            for (int i = 0; i + 1 < mapped.Length; i += 2)
            {
                minX = Math.Min(minX, mapped[i]);
                maxX = Math.Max(maxX, mapped[i]);
                minY = Math.Min(minY, mapped[i + 1]);
                maxY = Math.Max(maxY, mapped[i + 1]);
            }
        }

        return new BoundingBox(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Element name and attributes for the live node, used by actionability checks.</summary>
    internal async ValueTask<DomNode?> GetNodeInfoAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        NodeDescription description = await context.Session
            .SendAsync<NodeDescription, DescribeNodeParams>(
                "DOM.describeNode"u8,
                new DescribeNodeParams(RealNodeId(nodeId), depth: 0, pierce: false),
                cancellationToken)
            .ConfigureAwait(false);

        return DomTreeParser.Parse(description.NodeJson.Span);
    }

    internal async ValueTask ClickNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        (double x, double y) = await GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _input.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ClickNodeAsync(
        long nodeId,
        string button,
        int clickCount,
        TimeSpan? delay,
        int modifiers,
        CancellationToken cancellationToken)
    {
        (double x, double y) = await GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _input
            .ClickAsync(x, y, button, clickCount, delay, modifiers, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask HoverNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        (double x, double y) = await GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _input.HoverAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask HoverNodeAtAsync(
        double x,
        double y,
        int modifiers,
        CancellationToken cancellationToken)
        => _input.HoverAsync(x, y, modifiers, cancellationToken);

    internal ValueTask DragPointsAsync(
        double sourceX,
        double sourceY,
        double targetX,
        double targetY,
        CancellationToken cancellationToken)
        => _input.DragAsync(sourceX, sourceY, targetX, targetY, cancellationToken);

    internal async ValueTask FocusNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        await context.Session
            .SendAsync<Unit, FocusNodeParams>(
                "DOM.focus"u8,
                new FocusNodeParams(RealNodeId(nodeId)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<bool> IsCheckedAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            return false;
        }

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId, "function() { return this.checked === true; }", "[]", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Reading the checked state threw in the page.");
        }

        return result.Value.TryGetBoolean(out bool value) && value;
    }

    internal async ValueTask HighlightNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _overlayEnabled, 1) == 0)
        {
            await Session.SendAsync<Unit>("Overlay.enable"u8, cancellationToken).ConfigureAwait(false);
        }

        await Session
            .SendAsync<Unit, HighlightNodeParams>(
                "Overlay.highlightNode"u8,
                new HighlightNodeParams(nodeId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Hides the DevTools highlight overlay for this page.</summary>
    public async ValueTask HideHighlightAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (Volatile.Read(ref _overlayEnabled) == 0)
        {
            return;
        }

        await Session
            .SendAsync<Unit>("Overlay.hideHighlight"u8, cancellationToken)
            .ConfigureAwait(false);
    }

    internal ValueTask<string[]> SelectOptionsAsync(
        long nodeId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
        => SelectOptionsAsync(nodeId, values, labels: null, indexes: null, cancellationToken);

    internal async ValueTask<string[]> SelectOptionsAsync(
        long nodeId,
        IReadOnlyList<string>? values,
        IReadOnlyList<string>? labels,
        IReadOnlyList<int>? indexes,
        CancellationToken cancellationToken)
    {
        DomNode? info = await GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (info is null || info.LocalName != "select")
        {
            throw new CdpException("SelectOption requires an element whose tag name is 'select'.");
        }

        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            throw new CdpException("The select element could not be resolved to a JavaScript object.");
        }

        const string Function = """
            function(values, labels, indexes) {
              const wantedValues = (values || []).map(value => String(value));
              const wantedLabels = (labels || []).map(value => String(value));
              const wantedIndexes = indexes || [];
              const selected = [];
              for (let index = 0; index < this.options.length; index++) {
                const option = this.options[index];
                const match = wantedValues.includes(option.value)
                  || wantedLabels.includes(option.label)
                  || wantedIndexes.includes(index);
                option.selected = match;
                if (match) selected.push(option.value);
              }
              this.dispatchEvent(new Event('input', { bubbles: true }));
              this.dispatchEvent(new Event('change', { bubbles: true }));
              return selected;
            }
            """;

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(
                resolved.ObjectId,
                Function,
                SelectArguments(values, labels, indexes),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Selecting options threw in the page.");
        }

        return ReadStringArray(result.Value.ValueJson.Span);
    }

    private static string SelectArguments(
        IReadOnlyList<string>? values,
        IReadOnlyList<string>? labels,
        IReadOnlyList<int>? indexes)
    {
        StringBuilder json = new();
        json.Append('[');
        AppendStringArgument(json, values);
        json.Append(',');
        AppendStringArgument(json, labels);
        json.Append(',');
        json.Append("{\"value\":[");
        if (indexes is { Count: > 0 })
        {
            for (int i = 0; i < indexes.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append(indexes[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        json.Append("]}");
        json.Append(']');
        return json.ToString();
    }

    private static void AppendStringArgument(StringBuilder json, IReadOnlyList<string>? values)
    {
        json.Append("{\"value\":[");
        if (values is { Count: > 0 })
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append(JsText.Quote(values[i]));
            }
        }

        json.Append("]}");
    }

}
