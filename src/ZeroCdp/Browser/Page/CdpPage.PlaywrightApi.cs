using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="CdpPage.EvalOnSelectorAsync"/>.</summary>
public sealed class PageEvalOnSelectorOptions
{
    /// <summary>
    /// When true, the selector must resolve to a single element. Defaults to false, matching
    /// Playwright's legacy selector APIs.
    /// </summary>
    public bool? Strict { get; init; }
}

/// <summary>Options for <see cref="CdpPage.AriaSnapshotAsync"/>.</summary>
public sealed class PageAriaSnapshotOptions
{
    /// <summary>
    /// Include bounding boxes. Accepted for API compatibility; this implementation does not emit
    /// box annotations.
    /// </summary>
    public bool? Boxes { get; init; }

    /// <summary>Maximum number of nested levels to include. Null includes the whole tree.</summary>
    public int? Depth { get; init; }

    /// <summary>
    /// Snapshot mode. <see cref="AriaSnapshotMode.Ai"/> is accepted for API compatibility; this
    /// implementation does not emit <c>[ref=...]</c> markers.
    /// </summary>
    public AriaSnapshotMode? Mode { get; init; }

    /// <summary>Maximum time to spend producing the snapshot. Null uses the page default.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.WaitForWorkerAsync"/>.</summary>
public sealed class PageWaitForWorkerOptions
{
    /// <summary>Only accept a worker for which this predicate returns true.</summary>
    public Func<Worker, bool>? Predicate { get; init; }

    /// <summary>How long to wait before throwing. Null uses the page default timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for <see cref="CdpPage.RunAndWaitForWorkerAsync"/>.</summary>
public sealed class PageRunAndWaitForWorkerOptions
{
    /// <summary>Only accept a worker for which this predicate returns true.</summary>
    public Func<Worker, bool>? Predicate { get; init; }

    /// <summary>How long to wait before throwing. Null uses the page default timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

public sealed partial class CdpPage
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> with the first element matching
    /// <paramref name="selector"/> as its argument. Evaluation is isolated by default.
    /// </summary>
    public async ValueTask<EvaluationResult> EvalOnSelectorAsync(
        string selector,
        string expression,
        PageEvalOnSelectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        ArgumentNullException.ThrowIfNull(expression);

        bool strict = options?.Strict ?? false;
        Locator locator = Locator(selector);
        if (!strict)
        {
            return await locator
                .First()
                .EvaluateAsync(expression, isolatedContext: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!StrictSelectors)
        {
            // Locator evaluation follows page strictness; enforce the explicit override here.
            long[] nodes = await locator.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (nodes.Length > 1)
            {
                throw new CdpStrictModeException(selector, nodes.Length);
            }

            if (nodes.Length == 0)
            {
                throw new ElementNotFoundException(selector);
            }

            return await EvaluateOnNodeAsync(nodes[0], expression, isolatedContext: true, cancellationToken)
                .ConfigureAwait(false);
        }

        return await locator
            .EvaluateAsync(expression, isolatedContext: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls <paramref name="expression"/> with every element matching <paramref name="selector"/>
    /// as an array argument. Evaluation is isolated by default.
    /// </summary>
    public ValueTask<EvaluationResult> EvalOnSelectorAllAsync(
        string selector,
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        ArgumentNullException.ThrowIfNull(expression);
        return Locator(selector).EvaluateAllAsync(expression, isolatedContext: true, cancellationToken);
    }

    /// <summary>Returns a Playwright-style YAML aria snapshot of the whole page.</summary>
    public ValueTask<string> AriaSnapshotAsync(CancellationToken cancellationToken = default)
        => AriaSnapshotAsync(options: null, cancellationToken);

    /// <summary>Returns a Playwright-style YAML aria snapshot of the whole page.</summary>
    public async ValueTask<string> AriaSnapshotAsync(
        PageAriaSnapshotOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (options?.Timeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }

        return await GetPageAriaSnapshotAsync(
                interestingOnly: true,
                options?.Depth,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Every request observed on this page so far, oldest first.</summary>
    public async ValueTask<IReadOnlyList<PageRequest>> RequestsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
        return _network.GetRequests();
    }

    /// <summary>Waits for the next dedicated, shared or service worker to attach to this page.</summary>
    public async ValueTask<Worker> WaitForWorkerAsync(
        PageWaitForWorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PageWaitForWorkerOptions();
        Func<Worker, bool> predicate = options.Predicate ?? (static _ => true);
        TimeSpan effective = options.Timeout ?? DefaultTimeout;

        foreach (Worker worker in Workers)
        {
            if (predicate(worker))
            {
                return worker;
            }
        }

        TaskCompletionSource<Worker> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Worker worker)
        {
            if (predicate(worker))
            {
                completion.TrySetResult(worker);
            }
        }

        Worker += Handler;
        try
        {
            return await completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No matching worker attached within {effective.TotalSeconds:0.##}s.");
        }
        finally
        {
            Worker -= Handler;
        }
    }

    /// <summary>Runs an action and returns the worker it creates.</summary>
    public async ValueTask<Worker> RunAndWaitForWorkerAsync(
        Func<ValueTask> action,
        PageRunAndWaitForWorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        options ??= new PageRunAndWaitForWorkerOptions();
        ValueTask<Worker> waiting = WaitForWorkerAsync(
            new PageWaitForWorkerOptions
            {
                Predicate = options.Predicate,
                Timeout = options.Timeout,
            },
            cancellationToken);

        await action().ConfigureAwait(false);
        return await waiting.ConfigureAwait(false);
    }
}
