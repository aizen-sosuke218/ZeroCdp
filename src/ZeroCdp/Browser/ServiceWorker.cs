using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>
/// A service worker registered by pages in a browser context. Service workers are owned by the
/// context rather than by a single page, matching Playwright's model.
/// </summary>
public sealed class ServiceWorker
{
    private readonly CdpSession _session;
    private int _closed;

    internal ServiceWorker(BrowserContext context, CdpSession session)
    {
        Context = context;
        _session = session;
        Url = session.Url;
    }

    public BrowserContext Context { get; }

    /// <summary>URL of the service worker script.</summary>
    public string Url { get; }

    public string TargetId => _session.TargetId;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Evaluates JavaScript in the service worker's main context.</summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
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

    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);
}
