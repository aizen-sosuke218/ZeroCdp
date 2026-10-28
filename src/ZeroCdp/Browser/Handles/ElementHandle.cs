using System.Diagnostics;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>The state waited for by <see cref="ElementHandle.WaitForElementStateAsync"/>.</summary>
public enum ElementHandleState
{
    Visible = 0,
    Hidden = 1,
    Stable = 2,
    Enabled = 3,
    Disabled = 4,
    Editable = 5,
}

/// <summary>Options for <see cref="ElementHandle.WaitForSelectorAsync"/>.</summary>
public sealed class ElementHandleWaitForSelectorOptions
{
    /// <summary>State the descendant must reach. Defaults to <see cref="WaitForSelectorState.Visible"/>.</summary>
    public WaitForSelectorState State { get; init; } = WaitForSelectorState.Visible;

    public TimeSpan? Timeout { get; init; }

    /// <summary>Throw when more than one descendant matches. Currently informational.</summary>
    public bool Strict { get; init; }
}

/// <summary>
/// A handle to a DOM element. Unlike a selector, a handle keeps pointing at the same element until
/// it is detached or disposed.
/// </summary>
public sealed class ElementHandle : JSHandle
{
    private readonly long _nodeId;

    internal ElementHandle(
        CdpPage page,
        long nodeId,
        int contextId,
        string? objectId = null)
        : base(page, objectId ?? string.Empty, contextId, "ElementHandle")
    {
        _nodeId = nodeId;
        if (objectId is null)
        {
            ObjectIdInternal = null;
        }
    }

    internal long NodeId => _nodeId;

    /// <summary>Resolves the element to a JavaScript object if it is not already resolved.</summary>
    private async ValueTask EnsureObjectIdAsync(CancellationToken cancellationToken)
    {
        if (ObjectIdInternal is not null)
        {
            return;
        }

        ResolveNodeResult resolved = await Page.ResolveNodeObjectAsync(_nodeId, cancellationToken)
            .ConfigureAwait(false);
        SetObjectId(resolved.ObjectId!);
    }

    protected override ValueTask EnsureResolvedAsync(CancellationToken cancellationToken)
        => EnsureObjectIdAsync(cancellationToken);

    /// <summary>
    /// Evaluates a function with this element as its argument in the isolated world by default.
    /// The element is resolved again in the requested world, so switching worlds never reuses an
    /// object id that belongs to the other world.
    /// </summary>
    public override ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
        => Page.EvaluateOnNodeAsync(_nodeId, expression, isolatedContext, cancellationToken);

    /// <summary>Evaluates a function with this element as its argument and keeps the result.</summary>
    public override ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
        => Page.EvaluateHandleOnNodeAsync(_nodeId, expression, isolatedContext, cancellationToken);

    /// <summary>
    /// Evaluates an expression with the first descendant matching <paramref name="selector"/> as
    /// its argument. Evaluation is isolated by default.
    /// </summary>
    public async ValueTask<EvaluationResult> EvalOnSelectorAsync(
        string selector,
        string expression,
        bool isolatedContext = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        ElementHandle? handle = await QuerySelectorAsync(selector, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            throw new CdpException($"No element matches selector '{selector}'.");
        }

        try
        {
            return await handle.EvaluateAsync(expression, isolatedContext, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Calls a function with every descendant matching <paramref name="selector"/>. Evaluation is
    /// isolated by default.
    /// </summary>
    public async ValueTask<EvaluationResult> EvalOnSelectorAllAsync(
        string selector,
        string expression,
        bool isolatedContext = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        ArgumentNullException.ThrowIfNull(expression);
        IReadOnlyList<ElementHandle> handles = await QuerySelectorAllAsync(selector, cancellationToken)
            .ConfigureAwait(false);
        long[] nodeIds = new long[handles.Count];
        try
        {
            for (int i = 0; i < handles.Count; i++)
            {
                nodeIds[i] = handles[i].NodeId;
            }

            return await Page.EvaluateOnNodesAsync(nodeIds, expression, isolatedContext, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (ElementHandle handle in handles)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits for a descendant matching <paramref name="selector"/> to reach a state.</summary>
    public async ValueTask<ElementHandle?> WaitForSelectorAsync(
        string selector,
        ElementHandleWaitForSelectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        options ??= new ElementHandleWaitForSelectorOptions();
        TimeSpan timeout = options.Timeout ?? TimeSpan.FromSeconds(30);
        long start = Stopwatch.GetTimestamp();
        WaitForState state = options.State.ToWaitForState();

        while (true)
        {
            ElementHandle? handle = await QuerySelectorAsync(selector, cancellationToken).ConfigureAwait(false);
            bool satisfied;
            switch (state)
            {
                case WaitForState.Attached:
                    satisfied = handle is not null;
                    break;
                case WaitForState.Detached:
                    satisfied = handle is null;
                    break;
                case WaitForState.Hidden:
                    satisfied = handle is null
                        || !await handle.IsVisibleAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    satisfied = handle is not null
                        && await handle.IsVisibleAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }

            if (satisfied)
            {
                return handle;
            }

            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }

            if (Stopwatch.GetElapsedTime(start) >= timeout)
            {
                throw new CdpTimeoutException(
                    $"Selector '{selector}' did not reach {state} within {timeout.TotalSeconds:0.##}s.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<JSHandle?> QuerySelectorHandleAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        await EnsureObjectIdAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await Page.CallOnObjectAsync(
                ObjectIdInternal!,
                ContextId,
                "function(selector) { return this.querySelector(selector); }",
                JsText.StringArguments([selector]),
                returnByReference: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "querySelector threw in the page.");
        }

        if (string.IsNullOrEmpty(result.Value.ObjectId))
        {
            return null;
        }

        return new JSHandle(Page, result.Value, ContextId);
    }

    public async ValueTask<ElementHandle?> QuerySelectorAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        JSHandle? handle = await QuerySelectorHandleAsync(selector, cancellationToken).ConfigureAwait(false);
        return handle is null ? null : await handle.AsElementAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ElementHandle>> QuerySelectorAllAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        await EnsureObjectIdAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await Page.CallOnObjectAsync(
                ObjectIdInternal!,
                ContextId,
                "function(selector) { return Array.from(this.querySelectorAll(selector)); }",
                JsText.StringArguments([selector]),
                returnByReference: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "querySelectorAll threw in the page.");
        }

        if (string.IsNullOrEmpty(result.Value.ObjectId))
        {
            return [];
        }

        JSHandle array = new(Page, result.Value, ContextId);
        try
        {
            EvaluationResult lengthResult = await array
                .EvaluateAsync("values => values.length", cancellationToken)
                .ConfigureAwait(false);
            if (!lengthResult.Value.TryGetInt64(out long length))
            {
                return [];
            }

            List<ElementHandle> elements = new();
            for (int i = 0; i < length; i++)
            {
                JSHandle item = await array.GetPropertyAsync(
                        i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        cancellationToken)
                    .ConfigureAwait(false);
                ElementHandle? element = await item.AsElementAsync(cancellationToken).ConfigureAwait(false);
                if (element is not null)
                {
                    elements.Add(element);
                }
                else
                {
                    await item.DisposeAsync().ConfigureAwait(false);
                }
            }

            return elements;
        }
        finally
        {
            await array.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask ClickAsync(
        LocatorClickOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocatorClickOptions();
        if (options.Trial)
        {
            return;
        }

        await Page.ScrollToNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        (double x, double y) = await Page.GetNodeCenterAsync(_nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (options.Position is { } position)
        {
            BoundingBox? box = await Page.GetNodeBoundingBoxAsync(_nodeId, cancellationToken)
                .ConfigureAwait(false);
            if (box is { } value)
            {
                x = value.X + position.X;
                y = value.Y + position.Y;
            }
        }

        string button = options.Button switch
        {
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "left",
        };
        await Page.Input
            .ClickAsync(x, y, button, options.ClickCount, options.Delay, (int)options.Modifiers, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DblClickAsync(
        LocatorClickOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocatorClickOptions();
        return ClickAsync(
            new LocatorClickOptions
            {
                Button = options.Button,
                ClickCount = 2,
                Delay = options.Delay,
                Modifiers = options.Modifiers,
                Timeout = options.Timeout,
                Force = options.Force,
                Trial = options.Trial,
                Position = options.Position,
            },
            cancellationToken);
    }

    public async ValueTask HoverAsync(
        LocatorHoverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocatorHoverOptions();
        if (options.Trial)
        {
            return;
        }

        await Page.ScrollToNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        (double x, double y) = await Page.GetNodeCenterAsync(_nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (options.Position is { } position)
        {
            BoundingBox? box = await Page.GetNodeBoundingBoxAsync(_nodeId, cancellationToken)
                .ConfigureAwait(false);
            if (box is { } value)
            {
                x = value.X + position.X;
                y = value.Y + position.Y;
            }
        }

        await Page.Input.HoverAsync(x, y, (int)options.Modifiers, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FillAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await Page.FocusNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        await Page.SetNodeInputValueAsync(_nodeId, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await Page.FocusNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        await Page.SetNodeInputValueAsync(_nodeId, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask TypeAsync(
        string text,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        await Page.FocusNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        await Page.Keyboard.TypeAsync(text, delay, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PressAsync(
        string key,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        await Page.FocusNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        await Page.Keyboard.PressAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FocusAsync(CancellationToken cancellationToken = default)
        => await Page.FocusNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask BlurAsync(CancellationToken cancellationToken = default)
        => await Page.BlurNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask ScrollIntoViewIfNeededAsync(CancellationToken cancellationToken = default)
        => await Page.ScrollToNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask TapAsync(CancellationToken cancellationToken = default)
    {
        await Page.ScrollToNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        (double x, double y) = await Page.GetNodeCenterAsync(_nodeId, cancellationToken)
            .ConfigureAwait(false);
        await Page.Touchscreen.TapAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Playwright-style tap overload using an options object.</summary>
    public async ValueTask TapAsync(
        LocatorTapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await Page.ScrollToNodeAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        (double x, double y) = await Page.GetNodeCenterAsync(_nodeId, cancellationToken)
            .ConfigureAwait(false);
        if (options.Position is { } position)
        {
            BoundingBox? box = await Page.GetNodeBoundingBoxAsync(_nodeId, cancellationToken)
                .ConfigureAwait(false);
            if (box is { } value)
            {
                x = value.X + position.X;
                y = value.Y + position.Y;
            }
        }

        if (options.Trial)
        {
            return;
        }

        await Page.Touchscreen.TapAsync(x, y, (int)options.Modifiers, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
    {
        if (await Page.IsCheckedAsync(_nodeId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ClickAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Playwright-style check overload using an options object.</summary>
    public ValueTask CheckAsync(
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(desired: true, options, cancellationToken);
    }

    public async ValueTask UncheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await Page.IsCheckedAsync(_nodeId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await ClickAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Playwright-style uncheck overload using an options object.</summary>
    public ValueTask UncheckAsync(
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(desired: false, options, cancellationToken);
    }

    public async ValueTask SetCheckedAsync(
        bool checkedState,
        CancellationToken cancellationToken = default)
    {
        if (checkedState)
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UncheckAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Playwright-style set-checked overload using an options object.</summary>
    public ValueTask SetCheckedAsync(
        bool checkedState,
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(checkedState, options, cancellationToken);
    }

    private async ValueTask SetCheckedCoreAsync(
        bool desired,
        LocatorCheckOptions options,
        CancellationToken cancellationToken)
    {
        if (await Page.IsCheckedAsync(_nodeId, cancellationToken).ConfigureAwait(false) == desired)
        {
            return;
        }

        await ClickAsync(
                new LocatorClickOptions
                {
                    Force = options.Force,
                    Trial = options.Trial,
                    Position = options.Position,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string[]> SelectOptionAsync(
        params string[] values)
        => await Page.SelectOptionsAsync(_nodeId, values, CancellationToken.None).ConfigureAwait(false);

    public async ValueTask<string[]> SelectOptionAsync(
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
        => await Page.SelectOptionsAsync(_nodeId, values, cancellationToken).ConfigureAwait(false);

    public async ValueTask SetInputFilesAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
        => await Page.SetFileInputFilesAsync(_nodeId, files, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> TextContentAsync(CancellationToken cancellationToken = default)
        => await Page.NodeTextAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> InnerTextAsync(CancellationToken cancellationToken = default)
        => await Page.GetInnerTextAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    /// <summary>Playwright-style <c>innerHTML</c> of the element.</summary>
    public async ValueTask<string> InnerHTMLAsync(CancellationToken cancellationToken = default)
    {
        EvaluationResult result = await Page
            .EvaluateOnNodeAsync(_nodeId, "node => node.innerHTML", cancellationToken)
            .ConfigureAwait(false);
        return result.Value.StringValue ?? string.Empty;
    }

    /// <summary>
    /// Dispatches a DOM event on the element. Events are untrusted, matching
    /// <c>Element.dispatchEvent</c>; use <see cref="ClickAsync(LocatorClickOptions?, CancellationToken)"/>
    /// for trusted input.
    /// </summary>
    public async ValueTask<bool> DispatchEventAsync(
        string type,
        bool bubbles = true,
        bool cancelable = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        return await Page
            .DispatchNodeEventAsync(_nodeId, type, bubbles, cancelable, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Selects the text of an input, textarea or contenteditable element.</summary>
    public async ValueTask SelectTextAsync(CancellationToken cancellationToken = default)
        => await Page.SelectNodeTextAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string?> GetAttributeAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        Dom.DomNode? info = await Page.GetNodeInfoAsync(_nodeId, cancellationToken).ConfigureAwait(false);
        return info?.GetAttribute(name);
    }

    public async ValueTask<string?> InputValueAsync(CancellationToken cancellationToken = default)
        => await Page.GetInputValueAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> IsVisibleAsync(CancellationToken cancellationToken = default)
        => await Page.HasBoxAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> IsHiddenAsync(CancellationToken cancellationToken = default)
        => !await IsVisibleAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<BoundingBox?> BoundingBoxAsync(CancellationToken cancellationToken = default)
        => await Page.GetNodeBoundingBoxAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> IsCheckedAsync(CancellationToken cancellationToken = default)
        => await Page.IsCheckedAsync(_nodeId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<Frame?> OwnerFrameAsync(CancellationToken cancellationToken = default)
    {
        FrameContext context = Page.ContextForNode(_nodeId);
        return Page.FindFrameById(context.FrameId);
    }

    public async ValueTask<Frame?> ContentFrameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string frameId = await Page.GetFrameIdAsync(_nodeId, cancellationToken).ConfigureAwait(false);
            return Page.FindFrameById(frameId);
        }
        catch (CdpException)
        {
            return null;
        }
    }

    public async ValueTask<byte[]> ScreenshotBytesAsync(
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => await Page.ScreenshotNodeBytesAsync(_nodeId, options, cancellationToken).ConfigureAwait(false);

    public async ValueTask ScreenshotAsync(
        string path,
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WaitForElementStateAsync(
        ElementHandleState state,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = timeout ?? TimeSpan.FromSeconds(30);
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            bool satisfied = state switch
            {
                ElementHandleState.Visible => await IsVisibleAsync(cancellationToken).ConfigureAwait(false),
                ElementHandleState.Hidden => await IsHiddenAsync(cancellationToken).ConfigureAwait(false),
                ElementHandleState.Stable => await IsStableAsync(cancellationToken).ConfigureAwait(false),
                ElementHandleState.Enabled => await IsEnabledAsync(cancellationToken).ConfigureAwait(false),
                ElementHandleState.Disabled => !await IsEnabledAsync(cancellationToken).ConfigureAwait(false),
                ElementHandleState.Editable => await IsEditableAsync(cancellationToken).ConfigureAwait(false),
                _ => false,
            };

            if (satisfied)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpAssertionException(
                    $"Element did not become {state} within {effective.TotalMilliseconds:0} ms.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> IsStableAsync(CancellationToken cancellationToken)
    {
        BoundingBox? first = await BoundingBoxAsync(cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return false;
        }

        await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        BoundingBox? second = await BoundingBoxAsync(cancellationToken).ConfigureAwait(false);
        return second is { } value
            && Math.Abs(first.Value.X - value.X) < 0.5
            && Math.Abs(first.Value.Y - value.Y) < 0.5
            && Math.Abs(first.Value.Width - value.Width) < 0.5
            && Math.Abs(first.Value.Height - value.Height) < 0.5;
    }

    private async ValueTask<bool> IsEnabledCoreAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await Page.NodePropertyAsync(
                _nodeId,
                "function() { return !this.disabled && this.getAttribute('aria-disabled') !== 'true'; }",
                cancellationToken)
            .ConfigureAwait(false);
        return !result.Threw && result.Value.TryGetBoolean(out bool enabled) && enabled;
    }

    private async ValueTask<bool> IsEditableCoreAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await Page.NodePropertyAsync(
                _nodeId,
                "function() { const tag = this.tagName.toLowerCase(); return (tag === 'input' || tag === 'textarea' || this.isContentEditable) && !this.disabled && !this.readOnly; }",
                cancellationToken)
            .ConfigureAwait(false);
        return !result.Threw && result.Value.TryGetBoolean(out bool editable) && editable;
    }

    /// <summary>Whether the element is not disabled.</summary>
    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
        => IsEnabledCoreAsync(cancellationToken);

    /// <summary>Whether the element is disabled.</summary>
    public async ValueTask<bool> IsDisabledAsync(CancellationToken cancellationToken = default)
        => !await IsEnabledCoreAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the element is editable.</summary>
    public ValueTask<bool> IsEditableAsync(CancellationToken cancellationToken = default)
        => IsEditableCoreAsync(cancellationToken);
}
