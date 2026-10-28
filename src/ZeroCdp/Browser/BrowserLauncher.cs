using System.Diagnostics;
using System.Globalization;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Transport;

namespace ZeroCdp.Browser;

/// <summary>Starts a Chromium-family browser and connects to it over CDP.</summary>
public static class BrowserLauncher
{
    private const string ActivePortFileName = "DevToolsActivePort";

    /// <summary>
    /// Launches a browser with the default <see cref="LaunchOptions"/> and connects over CDP.
    /// </summary>
    public static ValueTask<LaunchedBrowser> LaunchAsync(
        CancellationToken cancellationToken = default)
        => LaunchAsync(new LaunchOptions(), connectionOptions: null, cancellationToken);

    /// <summary>
    /// Launches a browser with a private profile and connects over CDP.
    /// </summary>
    /// <remarks>
    /// The default transport is the DevTools pipe on every supported platform, so no listening
    /// socket is left behind. POSIX uses child fds 3/4; Windows passes two inherited anonymous-pipe
    /// handles through <c>--remote-debugging-io-pipes</c>. Use
    /// <see cref="CdpTransportKind.Port"/> when a loopback WebSocket endpoint is required.
    /// </remarks>
    public static async ValueTask<LaunchedBrowser> LaunchAsync(
        LaunchOptions options,
        CdpConnectionOptions? connectionOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.DisableSandbox)
        {
            Trace.TraceWarning(
                "ZeroCDP: LaunchOptions.DisableSandbox is set. The Chromium sandbox is a real security " +
                "boundary; only disable it for trusted content in a disposable environment.");
        }

        string executable = options.ExecutablePath
            ?? BrowserLocator.Find()
            ?? throw new CdpException(
                "No Chromium-family browser was found. Install one, or set ZEROCDP_CHROME_PATH.");

        bool usePipe = options.Transport switch
        {
            CdpTransportKind.Pipe => true,
            CdpTransportKind.Port => false,
            _ => true,
        };

        bool privateProfile = options.UserDataDirectory is null;
        string userDataDirectory = options.UserDataDirectory ?? ProfileDirectory.Create();
        bool keepProfile = options.KeepProfile || !privateProfile;

        try
        {
            return usePipe
                ? await LaunchOverPipeAsync(executable, userDataDirectory, options, connectionOptions, keepProfile, cancellationToken)
                    .ConfigureAwait(false)
                : await LaunchOverPortAsync(executable, userDataDirectory, options, connectionOptions, keepProfile, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch
        {
            if (!keepProfile)
            {
                await ProfileDirectory.DeleteAsync(userDataDirectory).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async ValueTask<LaunchedBrowser> LaunchOverPipeAsync(
        string executable,
        string userDataDirectory,
        LaunchOptions options,
        CdpConnectionOptions? connectionOptions,
        bool keepProfile,
        CancellationToken cancellationToken)
    {
        List<string> arguments = LaunchArguments.Build(options, userDataDirectory, usePipe: true);
        IPipeBrowserProcess process = OperatingSystem.IsWindows()
            ? WindowsBrowserProcess.Start(executable, arguments, options.InheritBrowserOutput)
            : PosixBrowserProcess.Start(executable, arguments, options.InheritBrowserOutput);
        try
        {
            StreamTransport transport = new(process.Input, process.Output, process.TransportDescription);
            CdpConnection connection = CdpConnection.Create(transport, connectionOptions);

            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(options.StartupTimeout);

            try
            {
                BrowserVersion version = await connection.SendAsync<BrowserVersion>("Browser.getVersion"u8, startup.Token)
                    .ConfigureAwait(false);

                if (version.Product.IsEmpty)
                {
                    throw new CdpException("The browser did not report a product version over CDP.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new CdpException(
                    $"The browser did not answer on the DevTools pipe within {options.StartupTimeout.TotalSeconds:0}s.");
            }

            CdpBrowser browser = await CdpBrowser.CreateAsync(connection, options.NavigationPolicy, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LaunchedBrowser(process, connection, browser, userDataDirectory, keepProfile, process.Drain);
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }
    }

    private static async ValueTask<LaunchedBrowser> LaunchOverPortAsync(
        string executable,
        string userDataDirectory,
        LaunchOptions options,
        CdpConnectionOptions? connectionOptions,
        bool keepProfile,
        CancellationToken cancellationToken)
    {
        List<string> arguments = LaunchArguments.Build(options, userDataDirectory, usePipe: false);

        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(startInfo)
            ?? throw new CdpException("The browser process could not be started.");

        ManagedBrowserProcess managed = new(process);
        try
        {
            // Drain both pipes: an unread redirect fills the OS buffer and freezes the browser.
            Task drain = ProcessDrain.DrainAsync(process);

            (int port, string browserPath) = await WaitForDebuggerAsync(
                process,
                userDataDirectory,
                options.StartupTimeout,
                cancellationToken).ConfigureAwait(false);

            string endpoint = $"ws://127.0.0.1:{port}{browserPath}";
            WebSocketTransport transport = await WebSocketTransport
                .ConnectAsync(new Uri(endpoint), cancellationToken)
                .ConfigureAwait(false);

            CdpConnection connection = CdpConnection.Create(transport, connectionOptions);

            BrowserVersion version = await connection.SendAsync<BrowserVersion>("Browser.getVersion"u8, cancellationToken)
                .ConfigureAwait(false);

            if (version.Product.IsEmpty)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new CdpException("The browser did not report a product version over CDP.");
            }

            CdpBrowser browser = await CdpBrowser.CreateAsync(connection, options.NavigationPolicy, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new LaunchedBrowser(managed, connection, browser, userDataDirectory, keepProfile, drain, endpoint);
        }
        catch
        {
            managed.Kill();
            managed.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attaches to an already-running Chromium browser over a browser-level CDP WebSocket.
    /// </summary>
    /// <remarks>
    /// This is the attach path for a browser the caller started (a real Chrome with a persistent
    /// profile, a remote container, or a browser launched with <c>--remote-debugging-port</c>).
    /// Disposing the returned browser disconnects: it closes the pages and contexts it created and
    /// leaves the browser process running, unlike disposing a <see cref="LaunchedBrowser"/>.
    /// </remarks>
    public static async ValueTask<CdpBrowser> ConnectOverCDPAsync(
        string webSocketEndpoint,
        CdpConnectionOptions? connectionOptions = null,
        ZeroCdp.Security.NavigationPolicy? navigationPolicy = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(webSocketEndpoint);

        Uri endpoint = new(webSocketEndpoint);
        if (endpoint.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("The endpoint must be a ws:// or wss:// URL.", nameof(webSocketEndpoint));
        }

        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));

        WebSocketTransport transport = await WebSocketTransport
            .ConnectAsync(endpoint, startup.Token)
            .ConfigureAwait(false);

        CdpConnection connection = CdpConnection.Create(transport, connectionOptions);
        try
        {
            BrowserVersion version = await connection
                .SendAsync<BrowserVersion>("Browser.getVersion"u8, startup.Token)
                .ConfigureAwait(false);

            if (version.Product.IsEmpty)
            {
                throw new CdpException("The endpoint did not answer with a browser version.");
            }

            return await CdpBrowser
                .CreateAsync(
                    connection,
                    navigationPolicy ?? ZeroCdp.Security.NavigationPolicy.AllowAll,
                    attached: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<(int Port, string BrowserPath)> WaitForDebuggerAsync(
        Process process,
        string userDataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string activePortFile = Path.Combine(userDataDirectory, ActivePortFileName);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new CdpException(
                    $"The browser exited with code {process.ExitCode} before exposing the DevTools endpoint.");
            }

            if (File.Exists(activePortFile))
            {
                string[] lines = await File.ReadAllLinesAsync(activePortFile, cancellationToken).ConfigureAwait(false);
                if (lines.Length >= 2
                    && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                    && port is > 0 and < 65536
                    && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                {
                    return (port, lines[1]);
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new CdpException(
                    "Timed out waiting for the browser to expose its DevTools endpoint. " +
                    "A sandbox or profile permission problem is the usual cause; " +
                    "LaunchOptions.DisableSandbox exists but weakens isolation.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

}
