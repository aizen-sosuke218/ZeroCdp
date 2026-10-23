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
    /// <summary>
    /// Raised when a popup opened by this page has been registered and its context options have
    /// been applied. The popup is also present in <see cref="BrowserContext.Pages"/>.
    /// </summary>
    public event Action<CdpPage>? PopupOpening
    {
        add
        {
            lock (_popupGate)
            {
                _popupHandlers += value;
            }
        }
        remove
        {
            lock (_popupGate)
            {
                _popupHandlers -= value;
            }
        }
    }

    /// <summary>
    /// Waits for the next popup opened by this page. Call this before the action that opens the
    /// popup, or use <see cref="RunAndWaitForPopupAsync"/>.
    /// </summary>
    public async ValueTask<CdpPage> WaitForPopupAsync(
        Func<CdpPage, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        TimeSpan effective = timeout ?? DefaultTimeout;
        PopupWaiter waiter = new(predicate);
        lock (_popupGate)
        {
            _popupWaiters.Add(waiter);
        }

        try
        {
            return await waiter.Completion.Task
                .WaitAsync(effective, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No popup opened by page '{TargetId}' within {effective.TotalSeconds:0.###}s.");
        }
        finally
        {
            lock (_popupGate)
            {
                _popupWaiters.Remove(waiter);
            }
        }
    }

    /// <summary>Runs an action and returns the popup it opens.</summary>
    public async ValueTask<CdpPage> RunAndWaitForPopupAsync(
        Func<ValueTask> action,
        Func<CdpPage, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValueTask<CdpPage> waiting = WaitForPopupAsync(predicate, timeout, cancellationToken);
        await action().ConfigureAwait(false);
        return await waiting.ConfigureAwait(false);
    }

    internal void NotifyPopup(CdpPage popup)
    {
        Action<CdpPage>? handlers;
        PopupWaiter[] waiters;
        lock (_popupGate)
        {
            handlers = _popupHandlers;
            waiters = _popupWaiters.ToArray();
        }

        foreach (PopupWaiter waiter in waiters)
        {
            try
            {
                if (waiter.Predicate is null || waiter.Predicate(popup))
                {
                    waiter.Completion.TrySetResult(popup);
                }
            }
            catch (Exception ex) when (ex is CdpException or InvalidOperationException)
            {
                waiter.Completion.TrySetException(ex);
            }
        }

        handlers?.Invoke(popup);
    }

    /// <summary>Raised on the reader thread when a download starts.</summary>
}
