using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>The kind of worker target attached to a page.</summary>
public enum WorkerKind
{
    Dedicated = 0,
    Shared = 1,
    Service = 2,
}

/// <summary>
/// A dedicated, shared or service worker owned by a page. Worker evaluation uses the worker's
/// main context and does not enable the Runtime domain.
/// </summary>
public sealed class Worker
{
    private readonly CdpSession _session;
    private int _closed;

    internal Worker(CdpPage page, CdpSession session)
    {
        Page = page;
        _session = session;
        Url = session.Url;
        Kind = session.TargetType switch
        {
            "service_worker" => WorkerKind.Service,
            "shared_worker" => WorkerKind.Shared,
            _ => WorkerKind.Dedicated,
        };
    }

    public CdpPage Page { get; }

    public string Url { get; }

    public WorkerKind Kind { get; }

    public string TargetId => _session.TargetId;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Evaluates JavaScript in the worker's main context.</summary>
    public ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(expression, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Evaluates JavaScript in the worker's main context. Workers have a single execution world,
    /// so <paramref name="isolatedContext"/> is accepted for Patchright API parity and ignored.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return await _session
            .SendAsync<EvaluationResult, EvaluateParams>(
                "Runtime.evaluate"u8,
                EvaluateParams.MainWorld(expression),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates JavaScript in the worker's main context and keeps the result as a handle.
    /// The handle is bound to the worker session, so its evaluation and disposal target the
    /// worker rather than the owning page.
    /// </summary>
    public ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
        => EvaluateHandleAsync(expression, isolatedContext: true, cancellationToken);

    /// <summary>
    /// Evaluates JavaScript in the worker's main context and keeps the result as a handle.
    /// Workers have a single execution world, so <paramref name="isolatedContext"/> is accepted
    /// for Patchright API parity and ignored.
    /// </summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        EvaluationResult result = await _session
            .SendAsync<EvaluationResult, EvaluateParams>(
                "Runtime.evaluate"u8,
                EvaluateParams.MainWorld(expression, returnByValue: false),
                cancellationToken)
            .ConfigureAwait(false);
        return JSHandle.FromWorkerResult(Page, result, _session);
    }

    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);
}
