using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    /// <summary>Playwright-style wait overload using an options object.</summary>
    public ValueTask WaitForAsync(
        LocatorWaitForOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WaitForAsync(options.State.ToWaitForState(), options.Timeout, cancellationToken);
    }

    /// <summary>Waits until the locator reaches <paramref name="state"/>.</summary>
    /// <exception cref="CdpTimeoutException">The state was not reached in time.</exception>
    public async ValueTask WaitForAsync(
        WaitForState state = WaitForState.Visible,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = EffectiveTimeout(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            bool satisfied;
            try
            {
                satisfied = await ProbeAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpCommandException)
            {
                // Node ids can be invalidated by a navigation or DOM replacement; poll again.
                satisfied = false;
            }

            if (satisfied)
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' did not become {state} within {effective.TotalSeconds:0.###}s.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask WaitForVisibleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => WaitForAsync(WaitForState.Visible, timeout, cancellationToken);

    public ValueTask WaitForHiddenAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => WaitForAsync(WaitForState.Hidden, timeout, cancellationToken);

    public ValueTask WaitForAttachedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => WaitForAsync(WaitForState.Attached, timeout, cancellationToken);

    public ValueTask WaitForDetachedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => WaitForAsync(WaitForState.Detached, timeout, cancellationToken);

    private async ValueTask<bool> ProbeAsync(WaitForState state, CancellationToken cancellationToken)
    {
        if (state == WaitForState.Detached)
        {
            try
            {
                return await CountAsync(cancellationToken).ConfigureAwait(false) == 0;
            }
            catch (ElementNotFoundException)
            {
                return true;
            }
        }

        long nodeId;
        try
        {
            nodeId = await FindFirstAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ElementNotFoundException)
        {
            return state == WaitForState.Hidden;
        }

        if (nodeId == 0)
        {
            return state == WaitForState.Hidden;
        }

        bool visible = await _page.HasBoxAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return state == WaitForState.Attached || (state == WaitForState.Visible ? visible : !visible);
    }

    private async ValueTask<long> WaitForActionAsync(
        bool requireVisible,
        bool requireEnabled,
        bool requireEditable,
        string requirement,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        TimeSpan effective = EffectiveTimeout(timeout);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            try
            {
                await _page.RunLocatorHandlersAsync(this, cancellationToken).ConfigureAwait(false);
                long nodeId = await FindSingleAsync(cancellationToken).ConfigureAwait(false);
                if (nodeId != 0)
                {
                    DomNode? info = requireEnabled || requireEditable
                        ? await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false)
                        : null;

                    bool enabled = !requireEnabled || (info is not null && !IsDisabled(info));
                    bool editable = !requireEditable || (info is not null && IsEditable(info));

                    if (enabled && editable)
                    {
                        if (!requireVisible
                            || await _page.HasBoxAsync(nodeId, cancellationToken).ConfigureAwait(false))
                        {
                            return nodeId;
                        }
                    }
                }
            }
            catch (ElementNotFoundException)
            {
                // The frame owner itself is missing; retry until it appears.
            }
            catch (CdpCommandException)
            {
                // The document changed between resolve and inspection; retry from the selector.
            }

            if (Stopwatch.GetElapsedTime(start) >= effective)
            {
                throw new CdpTimeoutException(
                    $"Locator '{Selector}' did not become {requirement} within {effective.TotalSeconds:0.###}s.");
            }

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<long> FindSingleAsync(CancellationToken cancellationToken)
    {
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        if (nodes.Length == 0)
        {
            return 0;
        }

        if (nodes.Length > 1 && _page.StrictSelectors)
        {
            throw new CdpStrictModeException(Selector, nodes.Length);
        }

        return nodes[0];
    }

    private TimeSpan EffectiveTimeout(TimeSpan? timeout)
        => timeout ?? Timeout ?? _page.DefaultTimeout;

    /// <summary>
    /// Retries a read whose node id can be invalidated by a navigation that commits between
    /// actionability polling and the read itself. Without this, a slow machine can observe a node
    /// from the old document and get "No node with given id found".
    /// </summary>
    private async ValueTask<T> RetryStaleNodeAsync<T>(
        Func<TimeSpan?, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = EffectiveTimeout(null);
        long start = Stopwatch.GetTimestamp();

        while (true)
        {
            TimeSpan remaining = effective - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.FromMilliseconds(1);
            }

            try
            {
                return await action(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpCommandException) when (Stopwatch.GetElapsedTime(start) < effective)
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
