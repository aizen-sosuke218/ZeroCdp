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
    internal async ValueTask<string> NodeTextAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        NodeDescription description = await context.Session
            .SendAsync<NodeDescription, DescribeNodeParams>(
                "DOM.describeNode"u8,
                new DescribeNodeParams(
                    RealNodeId(nodeId),
                    depth: context.Session.Connection.Options.MaxDomDepth,
                    pierce: true),
                cancellationToken)
            .ConfigureAwait(false);

        Dom.DomNode? node = Dom.DomTreeParser.Parse(description.NodeJson.Span);
        return node?.TextContent() ?? string.Empty;
    }

    /// <summary>Live <c>value</c> property of an input, textarea or select.</summary>
    internal async ValueTask<string?> GetInputValueAsync(long nodeId, CancellationToken cancellationToken)
        => await GetNodeStringPropertyAsync(
                nodeId,
                "function() { return this.value === undefined || this.value === null ? null : String(this.value); }",
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Rendered <c>innerText</c> of an element.</summary>
    internal async ValueTask<string> GetInnerTextAsync(long nodeId, CancellationToken cancellationToken)
        => await GetNodeStringPropertyAsync(
                nodeId,
                "function() { return this.innerText === undefined || this.innerText === null ? '' : String(this.innerText); }",
                cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;

    /// <summary>Sets an input/textarea value or contenteditable text without focus, for force fill.</summary>
    internal async ValueTask SetNodeInputValueAsync(
        long nodeId,
        string text,
        CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        string arguments = "[{\"value\":" + JsText.Quote(text) + "}]";

        const string Function = """
            function(value) {
              if (this.isContentEditable) {
                this.textContent = value;
              } else {
                this.value = value;
              }
              this.dispatchEvent(new Event('input', { bubbles: true }));
            }
            """;

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId!, Function, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Setting the element value threw in the page.");
        }
    }

    internal async ValueTask SelectNodeTextAsync(long nodeId, CancellationToken cancellationToken)
    {
        const string Function = """
            function() {
              if (typeof this.select === 'function') {
                this.focus();
                this.select();
                return true;
              }
              if (this.isContentEditable) {
                this.focus();
                const range = document.createRange();
                range.selectNodeContents(this);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                return true;
              }
              return false;
            }
            """;

        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId!, Function, "[]", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Selecting the element text threw in the page.");
        }

        if (!result.Value.TryGetBoolean(out bool selected) || !selected)
        {
            throw new CdpException("SelectText requires an input, textarea or contenteditable element.");
        }
    }

    internal async ValueTask BlurNodeAsync(long nodeId, CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId!, "function() { this.blur(); }", "[]", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Blurring the element threw in the page.");
        }
    }

    internal async ValueTask<bool> DispatchNodeEventAsync(
        long nodeId,
        string type,
        bool bubbles,
        bool cancelable,
        CancellationToken cancellationToken)
    {
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        string arguments = "[{\"value\":" + JsText.Quote(type)
            + "},{\"value\":" + (bubbles ? "true" : "false")
            + "},{\"value\":" + (cancelable ? "true" : "false") + "}]";

        const string Function = """
            function(type, bubbles, cancelable) {
              return this.dispatchEvent(new Event(type, { bubbles: bubbles, cancelable: cancelable }));
            }
            """;

        FrameContext context = ContextForNode(nodeId);
        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId!, Function, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Dispatching the event threw in the page.");
        }

        return result.Value.TryGetBoolean(out bool dispatched) && dispatched;
    }

    internal ValueTask<EvaluationResult> EvaluateOnNodeAsync(
        long nodeId,
        string expression,
        CancellationToken cancellationToken)
        => EvaluateOnNodeAsync(nodeId, expression, isolatedContext: true, cancellationToken);

    internal async ValueTask<EvaluationResult> EvaluateOnNodeAsync(
        long nodeId,
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, isolatedContext, cancellationToken)
            .ConfigureAwait(false);
        string function = "function() { const fn = (" + expression + "); return typeof fn === 'function' ? fn(this) : fn; }";

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId!, function, "[]", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Evaluating on the element threw in the page.");
        }

        return result;
    }

    private ValueTask<ResolveNodeResult> ResolveNodeAsync(
        long nodeId,
        CancellationToken cancellationToken)
        => ResolveNodeAsync(nodeId, isolatedContext: true, cancellationToken);

    private async ValueTask<ResolveNodeResult> ResolveNodeAsync(
        long nodeId,
        bool isolatedContext,
        CancellationToken cancellationToken)
    {
        if (!isolatedContext)
        {
            // No executionContextId means the node's own frame main world, which is exactly the
            // Patchright-compatible opt-in. This also covers same-process iframes.
            return await ResolveNodeCoreAsync(
                    ContextForNode(nodeId),
                    nodeId,
                    executionContextId: 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        FrameContext context = ContextForNode(nodeId);
        int executionContextId = await context.World
            .EnsureContextIdAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await ResolveNodeCoreAsync(context, nodeId, executionContextId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CdpCommandException ex) when (IsolatedWorldRuntime.IsStaleContext(ex))
        {
            // A navigation destroyed the cached world between context creation and resolution.
            context.World.Invalidate();
            int retryContextId = await context.World
                .EnsureContextIdAsync(cancellationToken)
                .ConfigureAwait(false);
            return await ResolveNodeCoreAsync(context, nodeId, retryContextId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<ResolveNodeResult> ResolveNodeCoreAsync(
        FrameContext context,
        long nodeId,
        int executionContextId,
        CancellationToken cancellationToken)
    {
        ResolveNodeResult resolved = await context.Session
            .SendAsync<ResolveNodeResult, ResolveNodeParams>(
                "DOM.resolveNode"u8,
                new ResolveNodeParams(RealNodeId(nodeId), executionContextId),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            throw new CdpException("The element could not be resolved to a JavaScript object.");
        }

        return resolved;
    }

    private async ValueTask<string?> GetNodeStringPropertyAsync(
        long nodeId,
        string functionDeclaration,
        CancellationToken cancellationToken)
    {
        FrameContext context = ContextForNode(nodeId);
        ResolveNodeResult resolved = await ResolveNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            return null;
        }

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(
                resolved.ObjectId,
                functionDeclaration,
                argumentsJson: "[]",
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Reading the element property threw in the page.");
        }

        return result.Value.StringValue;
    }
}
