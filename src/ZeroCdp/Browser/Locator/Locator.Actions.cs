using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    public async ValueTask FocusAsync(CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        await _page.FocusNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Highlights the first match using the DevTools overlay.</summary>
    public async ValueTask HighlightAsync(CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: true,
                requireEnabled: false,
                requireEditable: false,
                requirement: "visible",
                cancellationToken)
            .ConfigureAwait(false);

        await _page.HighlightNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes focus from the first match.</summary>
    public async ValueTask BlurAsync(CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        await _page.BlurNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ClickAsync(CancellationToken cancellationToken = default)
        => ClickAsync((ClickOptions?)null, cancellationToken);

    /// <summary>Playwright-style click overload using an options object.</summary>
    public ValueTask ClickAsync(
        LocatorClickOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ClickAsync(
            new ClickOptions
            {
                Button = options.Button,
                ClickCount = options.ClickCount,
                Delay = options.Delay,
                Modifiers = options.Modifiers,
                Timeout = options.Timeout,
                Force = options.Force,
                Trial = options.Trial,
                Position = options.Position,
            },
            cancellationToken);
    }

    public async ValueTask ClickAsync(
        ClickOptions? options,
        CancellationToken cancellationToken = default)
    {
        options ??= new ClickOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ClickCount, 1);

        if (options.Delay is { } delay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay.Ticks, 0);
        }

        string button = options.Button switch
        {
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "left",
        };

        TimeSpan effective = EffectiveTimeout(options.Timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long nodeId = await WaitForActionAsync(
                    requireVisible: !options.Force,
                    requireEnabled: !options.Force,
                    requireEditable: false,
                    requirement: options.Force ? "attached" : "visible and enabled",
                    cancellationToken,
                    options.Timeout)
                .ConfigureAwait(false);

            (double X, double Y)? point;
            if (options.Position is { } position)
            {
                BoundingBox? box = await _page
                    .GetNodeBoundingBoxAsync(nodeId, cancellationToken)
                    .ConfigureAwait(false);
                point = box is { } value
                    ? (value.X + position.X, value.Y + position.Y)
                    : null;
            }
            else if (options.Force)
            {
                point = await _page
                    .GetNodeCenterAsync(nodeId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                point = await _page
                    .TryGetActionPointAsync(nodeId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (point.HasValue)
            {
                if (options.Trial)
                {
                    return;
                }

                (double clickX, double clickY) = point.Value;
                await _page
                    .ClickNodeAtAsync(
                        clickX,
                        clickY,
                        button,
                        options.ClickCount,
                        options.Delay,
                        (int)options.Modifiers,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' stayed covered or moving for {effective.TotalSeconds:0.###}s, " +
                    "so the click was not dispatched.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Taps the first match with a trusted touch sequence.</summary>
    public ValueTask TapAsync(CancellationToken cancellationToken = default)
        => TapCoreAsync(
            timeout: null,
            position: null,
            force: false,
            trial: false,
            KeyModifiers.None,
            cancellationToken);

    /// <summary>Playwright-style tap overload using an options object.</summary>
    public ValueTask TapAsync(
        LocatorTapOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TapCoreAsync(
            options.Timeout,
            options.Position,
            options.Force,
            options.Trial,
            options.Modifiers,
            cancellationToken);
    }

    private async ValueTask TapCoreAsync(
        TimeSpan? timeout,
        Position? position,
        bool force,
        bool trial,
        KeyModifiers modifiers,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = EffectiveTimeout(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long nodeId = await WaitForActionAsync(
                    requireVisible: !force,
                    requireEnabled: !force,
                    requireEditable: false,
                    requirement: force ? "attached" : "visible and enabled",
                    cancellationToken,
                    timeout)
                .ConfigureAwait(false);

            (double X, double Y)? point;
            if (position is { } offset)
            {
                BoundingBox? box = await _page
                    .GetNodeBoundingBoxAsync(nodeId, cancellationToken)
                    .ConfigureAwait(false);
                point = box is { } value
                    ? (value.X + offset.X, value.Y + offset.Y)
                    : null;
            }
            else if (force)
            {
                point = await _page.GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                point = await _page.TryGetActionPointAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }

            if (point.HasValue)
            {
                if (trial)
                {
                    return;
                }

                (double tapX, double tapY) = point.Value;
                await _page
                    .Touchscreen
                    .TapAsync(tapX, tapY, (int)modifiers, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' stayed covered or moving for {effective.TotalSeconds:0.###}s, " +
                    "so the tap was not dispatched.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Double-clicks the first match using two trusted click sequences.</summary>
    public async ValueTask DblClickAsync(CancellationToken cancellationToken = default)
        => await ClickAsync(new ClickOptions { ClickCount = 2 }, cancellationToken).ConfigureAwait(false);

    /// <summary>Double-clicks using Playwright-style click options.</summary>
    public ValueTask DblClickAsync(
        ClickOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ClickAsync(
            new ClickOptions
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

    /// <summary>
    /// Drags the first match to another locator in the same page with trusted mouse input.
    /// </summary>
    public async ValueTask DragToAsync(
        Locator target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!ReferenceEquals(_page, target._page))
        {
            throw new ArgumentException("DragTo requires both locators to belong to the same page.", nameof(target));
        }

        long sourceNode = await WaitForActionAsync(
                requireVisible: true,
                requireEnabled: true,
                requireEditable: false,
                requirement: "visible and enabled",
                cancellationToken)
            .ConfigureAwait(false);

        // Resolve the target only after capturing the source centre: DOM.getDocument in the target
        // resolution invalidates previously handed out node ids.
        (double sourceX, double sourceY) = await _page
            .GetNodeCenterAsync(sourceNode, cancellationToken)
            .ConfigureAwait(false);

        long targetNode = await target.WaitForActionAsync(
                requireVisible: true,
                requireEnabled: false,
                requireEditable: false,
                requirement: "visible",
                cancellationToken)
            .ConfigureAwait(false);
        (double targetX, double targetY) = await _page
            .GetNodeCenterAsync(targetNode, cancellationToken)
            .ConfigureAwait(false);

        await _page
            .DragPointsAsync(sourceX, sourceY, targetX, targetY, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Playwright-style drag overload with positions, force and trial support.</summary>
    public async ValueTask DragToAsync(
        Locator target,
        LocatorDragToOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (!ReferenceEquals(_page, target._page))
        {
            throw new ArgumentException("DragTo requires both locators to belong to the same page.", nameof(target));
        }

        TimeSpan effective = EffectiveTimeout(options.Timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long sourceNode = await WaitForActionAsync(
                    requireVisible: !options.Force,
                    requireEnabled: !options.Force,
                    requireEditable: false,
                    requirement: options.Force ? "attached" : "visible and enabled",
                    cancellationToken,
                    options.Timeout)
                .ConfigureAwait(false);

            (double X, double Y)? sourcePoint = await ResolveActionPointAsync(
                    sourceNode,
                    options.SourcePosition,
                    options.Force,
                    cancellationToken)
                .ConfigureAwait(false);

            if (sourcePoint.HasValue)
            {
                long targetNode = await target.WaitForActionAsync(
                        requireVisible: true,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "visible",
                        cancellationToken,
                        options.Timeout)
                    .ConfigureAwait(false);

                (double X, double Y)? targetPoint = await ResolveActionPointAsync(
                        targetNode,
                        options.TargetPosition,
                        options.Force,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (targetPoint.HasValue)
                {
                    if (options.Trial)
                    {
                        return;
                    }

                    await _page
                        .DragPointsAsync(
                            sourcePoint.Value.X,
                            sourcePoint.Value.Y,
                            targetPoint.Value.X,
                            targetPoint.Value.Y,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' could not be dragged to '{target.Selector}' within " +
                    $"{effective.TotalSeconds:0.###}s.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<(double X, double Y)?> ResolveActionPointAsync(
        long nodeId,
        Position? offset,
        bool force,
        CancellationToken cancellationToken)
    {
        if (offset is { } position)
        {
            BoundingBox? box = await _page
                .GetNodeBoundingBoxAsync(nodeId, cancellationToken)
                .ConfigureAwait(false);
            return box is { } value ? (value.X + position.X, value.Y + position.Y) : null;
        }

        return force
            ? await _page.GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false)
            : await _page.TryGetActionPointAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask HoverAsync(CancellationToken cancellationToken = default)
        => HoverCoreAsync(
            timeout: null,
            position: null,
            force: false,
            trial: false,
            KeyModifiers.None,
            cancellationToken);

    /// <summary>Playwright-style hover overload using an options object.</summary>
    public ValueTask HoverAsync(
        LocatorHoverOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return HoverCoreAsync(
            options.Timeout,
            options.Position,
            options.Force,
            options.Trial,
            options.Modifiers,
            cancellationToken);
    }

    private async ValueTask HoverCoreAsync(
        TimeSpan? timeout,
        Position? position,
        bool force,
        bool trial,
        KeyModifiers modifiers,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = EffectiveTimeout(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            long nodeId = await WaitForActionAsync(
                    requireVisible: !force,
                    requireEnabled: false,
                    requireEditable: false,
                    requirement: force ? "attached" : "visible",
                    cancellationToken,
                    timeout)
                .ConfigureAwait(false);

            (double X, double Y)? point;
            if (position is { } offset)
            {
                BoundingBox? box = await _page
                    .GetNodeBoundingBoxAsync(nodeId, cancellationToken)
                    .ConfigureAwait(false);
                point = box is { } value
                    ? (value.X + offset.X, value.Y + offset.Y)
                    : null;
            }
            else if (force)
            {
                point = await _page.GetNodeCenterAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                point = await _page.TryGetActionPointAsync(nodeId, cancellationToken).ConfigureAwait(false);
            }

            if (point.HasValue)
            {
                if (trial)
                {
                    return;
                }

                (double hoverX, double hoverY) = point.Value;
                await _page
                    .HoverNodeAtAsync(hoverX, hoverY, (int)modifiers, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' stayed covered or moving for {effective.TotalSeconds:0.###}s, " +
                    "so the hover was not dispatched.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask TypeAsync(
        string text,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
        => TypeCoreAsync(text, delay, timeout: null, cancellationToken);

    /// <summary>Playwright-style type overload using an options object.</summary>
    public ValueTask TypeAsync(
        string text,
        LocatorTypeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TypeCoreAsync(text, options.Delay, options.Timeout, cancellationToken);
    }

    private async ValueTask TypeCoreAsync(
        string text,
        TimeSpan? delay,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        long nodeId = await WaitForActionAsync(
                requireVisible: true,
                requireEnabled: true,
                requireEditable: true,
                requirement: "visible, enabled and editable",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        await _page.FocusNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _page.Input.TypeAsync(text, delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the element's text using trusted input: select all, delete, then insert the text.
    /// </summary>
    public ValueTask FillAsync(string text, CancellationToken cancellationToken = default)
        => FillCoreAsync(text, timeout: null, force: false, cancellationToken);

    /// <summary>Playwright-style fill overload using an options object.</summary>
    public ValueTask FillAsync(
        string text,
        LocatorFillOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FillCoreAsync(text, options.Timeout, options.Force, cancellationToken);
    }

    private async ValueTask FillCoreAsync(
        string text,
        TimeSpan? timeout,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        long nodeId = await WaitForActionAsync(
                requireVisible: !force,
                requireEnabled: !force,
                requireEditable: !force,
                requirement: force ? "attached" : "visible, enabled and editable",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        if (force)
        {
            await _page.SetNodeInputValueAsync(nodeId, text, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _page.FocusNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _page.Input.PressAsync(OperatingSystem.IsMacOS() ? "Meta+A" : "Control+A", cancellationToken)
            .ConfigureAwait(false);
        await _page.Input.PressAsync("Backspace", cancellationToken).ConfigureAwait(false);
        await _page.Input.InsertTextAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears an editable element using trusted input.</summary>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        => FillAsync(string.Empty, cancellationToken);

    /// <summary>Types text one character at a time with optional delay, without clearing first.</summary>
    public ValueTask PressSequentiallyAsync(
        string text,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
        => TypeAsync(text, delay, cancellationToken);

    /// <summary>Selects the text of an input, textarea or contenteditable element.</summary>
    public async ValueTask SelectTextAsync(CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        await _page.SelectNodeTextAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask PressAsync(string key, CancellationToken cancellationToken = default)
        => PressCoreAsync(key, delay: null, timeout: null, cancellationToken);

    /// <summary>Playwright-style press overload using an options object.</summary>
    public ValueTask PressAsync(
        string key,
        LocatorPressOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PressCoreAsync(key, options.Delay, options.Timeout, cancellationToken);
    }

    private async ValueTask PressCoreAsync(
        string key,
        TimeSpan? delay,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: true,
                requireEnabled: true,
                requireEditable: false,
                requirement: "visible and enabled",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        await _page.FocusNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        await _page.Input.PressAsync(key, delay, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
        => await SetCheckedCoreAsync(desired: true, options: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Playwright-style check overload using an options object.</summary>
    public ValueTask CheckAsync(
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(desired: true, options, cancellationToken);
    }

    public async ValueTask UncheckAsync(CancellationToken cancellationToken = default)
        => await SetCheckedCoreAsync(desired: false, options: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Playwright-style uncheck overload using an options object.</summary>
    public ValueTask UncheckAsync(
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(desired: false, options, cancellationToken);
    }

    /// <summary>Checks or unchecks a checkbox or radio button.</summary>
    public ValueTask SetCheckedAsync(bool checkedState, CancellationToken cancellationToken = default)
        => SetCheckedCoreAsync(checkedState, options: null, cancellationToken);

    /// <summary>Checks or unchecks a checkbox or radio button with Playwright-style options.</summary>
    public ValueTask SetCheckedAsync(
        bool checkedState,
        LocatorCheckOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetCheckedCoreAsync(checkedState, options, cancellationToken);
    }

    /// <summary>Selects options by value or label and fires native <c>input</c>/<c>change</c> events.</summary>
    public async ValueTask<string[]> SelectOptionAsync(
        params string[] values)
        => await SelectOptionAsync(values, CancellationToken.None).ConfigureAwait(false);

    public ValueTask<string[]> SelectOptionAsync(
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
        => SelectOptionCoreAsync(values, timeout: null, force: false, cancellationToken);

    /// <summary>Playwright-style select-option overload using an options object.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        IReadOnlyList<string> values,
        LocatorSelectOptionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SelectOptionCoreAsync(values, options.Timeout, options.Force, cancellationToken);
    }

    /// <summary>Selects options by value, label or zero-based index.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        LocatorSelectOptionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        int groups = (options.Values is { Count: > 0 } ? 1 : 0)
            + (options.Labels is { Count: > 0 } ? 1 : 0)
            + (options.Indexes is { Count: > 0 } ? 1 : 0);
        if (groups != 1)
        {
            throw new ArgumentException(
                "Exactly one of Values, Labels or Indexes must contain at least one entry.",
                nameof(options));
        }

        return SelectOptionGroupsCoreAsync(
            options.Values,
            options.Labels,
            options.Indexes,
            options.Timeout,
            options.Force,
            cancellationToken);
    }

    private async ValueTask<string[]> SelectOptionGroupsCoreAsync(
        IReadOnlyList<string>? values,
        IReadOnlyList<string>? labels,
        IReadOnlyList<int>? indexes,
        TimeSpan? timeout,
        bool force,
        CancellationToken cancellationToken)
    {
        if (indexes is { Count: > 0 })
        {
            foreach (int index in indexes)
            {
                if (index < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(indexes));
                }
            }
        }

        long nodeId = await WaitForActionAsync(
                requireVisible: !force,
                requireEnabled: !force,
                requireEditable: false,
                requirement: force ? "attached" : "visible and enabled",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        return await _page
            .SelectOptionsAsync(nodeId, values, labels, indexes, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<string[]> SelectOptionCoreAsync(
        IReadOnlyList<string> values,
        TimeSpan? timeout,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one option value or label is required.", nameof(values));
        }

        long nodeId = await WaitForActionAsync(
                requireVisible: !force,
                requireEnabled: !force,
                requireEditable: false,
                requirement: force ? "attached" : "visible and enabled",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        return await _page.SelectOptionsAsync(nodeId, values, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ScrollIntoViewAsync(CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        await _page.ScrollToNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Alias for <see cref="ScrollIntoViewAsync"/>.</summary>
    public ValueTask ScrollIntoViewIfNeededAsync(CancellationToken cancellationToken = default)
        => ScrollIntoViewAsync(cancellationToken);

    /// <summary>
    /// Dispatches a DOM event on the first match. Events are untrusted, matching
    /// <c>Element.dispatchEvent</c>; use <see cref="ClickAsync(CancellationToken)"/> for trusted
    /// input.
    /// </summary>
    public async ValueTask<bool> DispatchEventAsync(
        string type,
        bool bubbles = true,
        bool cancelable = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);

        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        return await _page
            .DispatchNodeEventAsync(nodeId, type, bubbles, cancelable, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates an expression with the first match as <c>this</c>. A function expression is
    /// called with the element as its argument.
    /// </summary>
    public ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(expression, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Evaluates an expression with the first match as <c>this</c>. Evaluation is isolated by
    /// default; pass <paramref name="isolatedContext"/> false for the frame's main world.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);

        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        return await _page
            .EvaluateOnNodeAsync(nodeId, expression, isolatedContext, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates an expression with the first match as <c>this</c> and returns a handle to the
    /// result. A function expression is called with the element as its argument.
    /// </summary>
    public ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
        => EvaluateHandleAsync(expression, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Evaluates an expression with the first match as <c>this</c> and returns a handle to the
    /// result. Evaluation is isolated by default.
    /// </summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);

        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);

        return await _page
            .EvaluateHandleOnNodeAsync(nodeId, expression, isolatedContext, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a function with an array of all matching elements. The function is evaluated in the
    /// frame's isolated world and receives the elements as its first argument.
    /// </summary>
    public ValueTask<EvaluationResult> EvaluateAllAsync(
        string functionDeclaration,
        CancellationToken cancellationToken = default)
        => EvaluateAllAsync(functionDeclaration, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Calls a function with an array of all matching elements. Evaluation is isolated by default.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateAllAsync(
        string functionDeclaration,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(functionDeclaration);
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        return await _page
            .EvaluateOnNodesAsync(nodes, functionDeclaration, isolatedContext, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Hides the DevTools highlight overlay for this page.</summary>
    public ValueTask HideHighlightAsync(CancellationToken cancellationToken = default)
        => _page.HideHighlightAsync(cancellationToken);

    /// <summary>Text content of the first match (<c>textContent</c>, not rendered text).</summary>

    private async ValueTask SetCheckedCoreAsync(
        bool desired,
        LocatorCheckOptions? options,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = EffectiveTimeout(options?.Timeout);
        long start = Stopwatch.GetTimestamp();
        bool clicked = false;
        bool force = options?.Force ?? false;

        while (true)
        {
            long nodeId = await WaitForActionAsync(
                    requireVisible: !force,
                    requireEnabled: !force,
                    requireEditable: false,
                    requirement: force ? "attached" : "visible and enabled",
                    cancellationToken,
                    options?.Timeout)
                .ConfigureAwait(false);

            DomNode? info = await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
            string? type = info?.GetAttribute("type");
            if (info is null || info.LocalName != "input"
                || (type is null || (!type.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
                    && !type.Equals("radio", StringComparison.OrdinalIgnoreCase))))
            {
                throw new CdpException("Check and Uncheck require an input element of type checkbox or radio.");
            }

            if (!desired && type is not null && type.Equals("radio", StringComparison.OrdinalIgnoreCase))
            {
                throw new CdpException("A radio button cannot be unchecked.");
            }

            bool current = await _page.IsCheckedAsync(nodeId, cancellationToken).ConfigureAwait(false);
            if (current == desired)
            {
                return;
            }

            if (options?.Trial == true)
            {
                return;
            }

            if (!clicked)
            {
                await ClickAsync(
                        new ClickOptions
                        {
                            Force = force,
                            Trial = false,
                            Position = options?.Position,
                            Timeout = options?.Timeout,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                clicked = true;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' did not become {(desired ? "checked" : "unchecked")} " +
                    $"within {effective.TotalSeconds:0.###}s.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
