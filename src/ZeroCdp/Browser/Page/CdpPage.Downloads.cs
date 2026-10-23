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
    /// <summary>Raised on the reader thread when a download starts.</summary>
    public event Action<Download>? DownloadOpening
    {
        add
        {
            lock (_downloadGate)
            {
                _downloadHandlers += value;
            }
        }
        remove
        {
            lock (_downloadGate)
            {
                _downloadHandlers -= value;
            }
        }
    }

    /// <summary>
    /// Waits for the next download started by this page. Call before the triggering action or use
    /// <see cref="RunAndWaitForDownloadAsync"/>. Chromium is put in <c>allowAndName</c> mode, and
    /// the file is written under <see cref="CdpBrowser.DownloadsDirectory"/>.
    /// </summary>
    public async ValueTask<Download> WaitForDownloadAsync(
        Func<Download, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureDownloadCaptureAsync(cancellationToken).ConfigureAwait(false);
        DownloadWaiter waiter = AddDownloadWaiter(predicate);
        return await WaitForDownloadCoreAsync(waiter, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an action and returns the download it starts.</summary>
    public async ValueTask<Download> RunAndWaitForDownloadAsync(
        Func<ValueTask> action,
        Func<Download, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await EnsureDownloadCaptureAsync(cancellationToken).ConfigureAwait(false);
        DownloadWaiter waiter = AddDownloadWaiter(predicate);
        try
        {
            await action().ConfigureAwait(false);
            return await WaitForDownloadCoreAsync(waiter, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RemoveDownloadWaiter(waiter);
            throw;
        }
    }

    private async ValueTask EnsureDownloadCaptureAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _downloadsConfigured) != 0)
        {
            return;
        }

        await EnableAsync(cancellationToken).ConfigureAwait(false);
        await Session
            .SendAsync<Unit, SetDownloadBehaviorParams>(
                "Page.setDownloadBehavior"u8,
                new SetDownloadBehaviorParams("allowAndName", _browser.DownloadsDirectory),
                cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _downloadsConfigured, 1);
    }

    private DownloadWaiter AddDownloadWaiter(Func<Download, bool>? predicate)
    {
        DownloadWaiter waiter = new(predicate);
        lock (_downloadGate)
        {
            _downloadWaiters.Add(waiter);
        }

        return waiter;
    }

    private void RemoveDownloadWaiter(DownloadWaiter waiter)
    {
        lock (_downloadGate)
        {
            _downloadWaiters.Remove(waiter);
        }
    }

    private async ValueTask<Download> WaitForDownloadCoreAsync(
        DownloadWaiter waiter,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = timeout ?? DefaultTimeout;
        try
        {
            return await waiter.Completion.Task
                .WaitAsync(effective, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No download started by page '{TargetId}' within {effective.TotalSeconds:0.###}s.");
        }
        finally
        {
            RemoveDownloadWaiter(waiter);
        }
    }

    private void OnDownloadWillBegin(DownloadBeginInfo info)
    {
        string path = System.IO.Path.Combine(_browser.DownloadsDirectory, info.Guid);
        Download download = new(this, info.Guid, info.Url, info.SuggestedFilename, path);

        Action<Download>? handlers;
        DownloadWaiter[] waiters;
        lock (_downloadGate)
        {
            _activeDownloads[info.Guid] = download;
            handlers = _downloadHandlers;
            waiters = _downloadWaiters.ToArray();
        }

        // Observers run before the waiter is completed so a test or application that records the
        // event and then awaits RunAndWaitForDownloadAsync sees the same instance.
        handlers?.Invoke(download);

        foreach (DownloadWaiter waiter in waiters)
        {
            try
            {
                if (waiter.Predicate is null || waiter.Predicate(download))
                {
                    waiter.Completion.TrySetResult(download);
                }
            }
            catch (Exception ex) when (ex is CdpException or InvalidOperationException)
            {
                waiter.Completion.TrySetException(ex);
            }
        }
    }

    private void OnDownloadProgress(DownloadProgressInfo progress)
    {
        Download? download;
        lock (_downloadGate)
        {
            _activeDownloads.TryGetValue(progress.Guid, out download);
            if (download is null)
            {
                return;
            }

            if (!progress.State.Equals("inProgress", StringComparison.Ordinal))
            {
                _activeDownloads.Remove(progress.Guid);
            }
        }

        download.ReportProgress(progress.ReceivedBytes, progress.TotalBytes, progress.State);
    }

    /// <summary>
    /// Raised on the reader thread after a file chooser was intercepted. Interception must first
    /// be enabled with <see cref="EnableFileChooserInterceptionAsync"/>; waiters enable it
    /// automatically.
    /// </summary>
}
