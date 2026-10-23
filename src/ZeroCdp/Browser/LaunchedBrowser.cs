using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>A running browser and its CDP connection.</summary>
public sealed class LaunchedBrowser : IAsyncDisposable
{
    private readonly IBrowserProcess _process;
    private readonly bool _keepProfile;
    private readonly string _userDataDirectory;
    private readonly Task _drain;
    private int _disposed;

    internal LaunchedBrowser(
        IBrowserProcess process,
        CdpConnection connection,
        CdpBrowser browser,
        string userDataDirectory,
        bool keepProfile,
        Task drain,
        string? webSocketEndpoint = null)
    {
        _process = process;
        Connection = connection;
        Browser = browser;
        _userDataDirectory = userDataDirectory;
        _keepProfile = keepProfile;
        _drain = drain;
        WebSocketEndpoint = webSocketEndpoint;
    }

    public CdpConnection Connection { get; }

    /// <summary>
    /// The browser-level WebSocket endpoint, when the browser was started in port mode. Null for a
    /// pipe launch, which by design has no endpoint to connect to.
    /// </summary>
    public string? WebSocketEndpoint { get; }

    /// <summary>Target and session management for this browser.</summary>
    public CdpBrowser Browser { get; }

    public int ProcessId => _process.Id;

    public string UserDataDirectory => _userDataDirectory;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Browser.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
        {
            // Pages may already be gone with the browser.
        }

        // Ask the browser to exit on its own first: a SIGKILL leaves the profile half written and
        // makes Chromium log a broken-pipe error. The kill below is the fallback.
        await CloseGracefullyAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
        _process.Kill();

        try
        {
            using CancellationTokenSource wait = new(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // The process is gone or refusing to cooperate; nothing else to do.
        }

        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Draining is best effort.
        }

        _process.Dispose();

        if (!_keepProfile)
        {
            await ProfileDirectory.DeleteAsync(_userDataDirectory).ConfigureAwait(false);
        }
    }

    private async ValueTask CloseGracefullyAsync()
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await Connection.SendAsync<ZeroCdp.Protocol.Unit>("Browser.close"u8, timeout.Token).ConfigureAwait(false);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone, or too slow: the kill path handles it.
        }
    }

}
