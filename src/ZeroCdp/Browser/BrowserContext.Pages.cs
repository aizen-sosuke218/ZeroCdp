using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    /// <summary>Playwright-style alias for <see cref="PageOpened"/>.</summary>
    public event Action<CdpPage>? Page
    {
        add => PageOpened += value;
        remove => PageOpened -= value;
    }

    /// <summary>
    /// Waits for the next page registered in this context, whether it was created through
    /// <see cref="NewPageAsync(string,CancellationToken)"/> or opened by another page. Mirrors
    /// Playwright's <c>context.WaitForEventAsync(context =&gt; context.Page)</c>.
    /// </summary>
    public async ValueTask<CdpPage> WaitForPageAsync(
        Func<CdpPage, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        TimeSpan effective = timeout ?? DefaultWaitTimeout;
        TaskCompletionSource<CdpPage> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<CdpPage> handler = page =>
        {
            try
            {
                if (predicate is null || predicate(page))
                {
                    completion.TrySetResult(page);
                }
            }
            catch (Exception ex) when (ex is CdpException or InvalidOperationException)
            {
                completion.TrySetException(ex);
            }
        };

        PageOpened += handler;
        try
        {
            return await completion.Task
                .WaitAsync(effective, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No page opened in context '{BrowserContextId ?? "default"}' within "
                + $"{effective.TotalSeconds:0.###}s.");
        }
        finally
        {
            PageOpened -= handler;
        }
    }

    /// <summary>Runs an action and returns the page it creates or opens.</summary>
    public async ValueTask<CdpPage> RunAndWaitForPageAsync(
        Func<ValueTask> action,
        Func<CdpPage, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValueTask<CdpPage> waiting = WaitForPageAsync(predicate, timeout, cancellationToken);
        await action().ConfigureAwait(false);
        return await waiting.ConfigureAwait(false);
    }
}
