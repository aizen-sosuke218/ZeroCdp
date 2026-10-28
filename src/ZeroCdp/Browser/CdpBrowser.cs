using System.Text;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Security;

namespace ZeroCdp.Browser;

/// <summary>
/// The browser endpoint: target creation, context isolation, session tracking and event
/// demultiplexing.
/// </summary>
/// <remarks>
/// One launch means one connection; every page and worker hangs off it as a flattened session
/// tagged with a session id. That is the same shape Playwright uses, and it means adding a page
/// costs a target and a session, not another socket or process.
/// </remarks>
public sealed partial class CdpBrowser : IAsyncDisposable
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(15);

    private static readonly string StorageBootstrapBodyBase64 = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("<!doctype html><html><body></body></html>"));

    private readonly CdpConnection _connection;
    private readonly SessionRouter _router;
    private readonly List<CdpPage> _pages = new();
    private readonly List<BrowserContext> _contexts = new();
    private readonly Dictionary<string, CdpPage> _sessionOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowserContext> _serviceWorkerContexts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Lock _pageEventGate = new();
    private Action<CdpSession>? _sessionAttached;
    private Action<CdpSession>? _sessionDetached;
    private Action<CdpPage>? _pageOpened;
    private Action<CdpPage>? _pageClosed;
    private string? _downloadsDirectory;
    private readonly bool _attached;
    private int _disposed;

    private CdpBrowser(CdpConnection connection, SessionRouter router, NavigationPolicy navigationPolicy, bool attached)
    {
        _connection = connection;
        _router = router;
        NavigationPolicy = navigationPolicy;
        _attached = attached;
        DefaultContext = new BrowserContext(this, browserContextId: null, options: null);
        _contexts.Add(DefaultContext);
        _router.SessionAttached = OnSessionAttached;
        _router.SessionDetached = OnSessionDetached;
    }

    public CdpConnection Connection => _connection;

    /// <summary>Browser product version reported by <c>Browser.getVersion</c>.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>True while the underlying transport is open.</summary>
    public bool IsConnected => !_connection.IsClosed;

    /// <summary>Allow-list applied before every top-level navigation.</summary>
    public NavigationPolicy NavigationPolicy { get; }

    /// <summary>Every live session, including ones the browser opened on its own.</summary>
    public IReadOnlyList<CdpSession> Sessions => _router.Snapshot();

    /// <summary>Every open page across all contexts.</summary>
    public IReadOnlyList<CdpPage> Pages
    {
        get
        {
            lock (_gate)
            {
                return _pages.ToArray();
            }
        }
    }

    /// <summary>Open browser contexts, including the default context.</summary>
    public IReadOnlyList<BrowserContext> Contexts
    {
        get
        {
            lock (_gate)
            {
                return _contexts.ToArray();
            }
        }
    }

    /// <summary>The initial context created by the browser process.</summary>
    public BrowserContext DefaultContext { get; }

    /// <summary>
    /// Directory where accepted downloads are written. It is shared by all pages in this browser
    /// and is created on first use; files are named by their CDP download GUID so concurrent
    /// downloads cannot overwrite each other.
    /// </summary>
    public string DownloadsDirectory
    {
        get
        {
            string? existing = Volatile.Read(ref _downloadsDirectory);
            if (existing is not null)
            {
                return existing;
            }

            lock (_gate)
            {
                if (_downloadsDirectory is null)
                {
                    string directory = Path.Combine(
                        Path.GetTempPath(),
                        "zerocdp-downloads",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    Volatile.Write(ref _downloadsDirectory, directory);
                }

                return _downloadsDirectory;
            }
        }
    }

    /// <summary>Browser-level events (no session id), for example <c>Target.targetCreated</c>.</summary>
    public ICdpEventSink? BrowserEventSink
    {
        get => _router.RootSink;
        set => _router.RootSink = value;
    }

    /// <summary>Raised for every target the browser attaches to, whoever created it.</summary>
    /// <remarks>
    /// The browser's own popup handling still runs first, so a handler set here sees
    /// <see cref="CdpBrowser.PageOpened"/> pages already registered.
    /// </remarks>
    public Action<CdpSession>? SessionAttached
    {
        get => _sessionAttached;
        set => _sessionAttached = value;
    }

    public Action<CdpSession>? SessionDetached
    {
        get => _sessionDetached;
        set => _sessionDetached = value;
    }

    /// <summary>
    /// Raised when a page registers in this browser: pages created through <c>NewPageAsync</c> and
    /// pages opened by the page itself, for example <c>window.open</c>. The page is present in
    /// <see cref="Pages"/> by the time the handler runs.
    /// </summary>
    public event Action<CdpPage>? PageOpened
    {
        add
        {
            lock (_pageEventGate)
            {
                _pageOpened += value;
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _pageOpened -= value;
            }
        }
    }

    /// <summary>Raised when a page target detaches or closes.</summary>
    public event Action<CdpPage>? PageClosed
    {
        add
        {
            lock (_pageEventGate)
            {
                _pageClosed += value;
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _pageClosed -= value;
            }
        }
    }

    public static ValueTask<CdpBrowser> CreateAsync(
        CdpConnection connection,
        CancellationToken cancellationToken = default)
        => CreateAsync(connection, NavigationPolicy.AllowAll, attached: false, cancellationToken);

    /// <summary>
    /// Takes over the connection's event routing, turns on flattened auto-attach and binds the
    /// navigation policy.
    /// </summary>
    /// <remarks>
    /// <c>waitForDebuggerOnStart</c> is on so a new target can be instrumented before it runs any
    /// page script; the router resumes each target as soon as its session exists.
    /// </remarks>
    public static async ValueTask<CdpBrowser> CreateAsync(
        CdpConnection connection,
        NavigationPolicy navigationPolicy,
        bool attached = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(navigationPolicy);

        SessionRouter router = new(connection);
        connection.EventSink = router;
        CdpBrowser browser = new(connection, router, navigationPolicy, attached);

        await connection
            .SendAsync<Unit, SetAutoAttachParams>(
                "Target.setAutoAttach"u8,
                new SetAutoAttachParams(autoAttach: true, waitForDebuggerOnStart: true, flatten: true),
                cancellationToken)
            .ConfigureAwait(false);

        BrowserVersion version = await connection
            .SendAsync<BrowserVersion>("Browser.getVersion"u8, cancellationToken)
            .ConfigureAwait(false);
        browser.Version = DecodeProductVersion(version.Product.Span);

        return browser;
    }

    /// <summary>Opens a new page in the default context.</summary>
    public ValueTask<CdpPage> NewPageAsync(string url = "about:blank", CancellationToken cancellationToken = default)
        => DefaultContext.NewPageAsync(url, cancellationToken);

    /// <summary>Opens a page with Playwright-style per-page emulation options.</summary>
    public ValueTask<CdpPage> NewPageAsync(
        BrowserNewPageOptions options,
        CancellationToken cancellationToken = default)
        => DefaultContext.NewPageAsync(options, cancellationToken);

    /// <summary>Opens a page at <paramref name="url"/> with per-page emulation options.</summary>
    public ValueTask<CdpPage> NewPageAsync(
        string url,
        BrowserNewPageOptions options,
        CancellationToken cancellationToken = default)
        => DefaultContext.NewPageAsync(url, options, cancellationToken);

    /// <summary>Playwright-style browser close. Same effect as <see cref="DisposeAsync"/>.</summary>
    public ValueTask CloseAsync() => DisposeAsync();


    private static string DecodeProductVersion(ReadOnlySpan<byte> productUtf8)
    {
        string product = Encoding.UTF8.GetString(productUtf8);
        int slash = product.IndexOf('/');
        return slash >= 0 && slash + 1 < product.Length ? product[(slash + 1)..] : product;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        BrowserContext[] contexts;
        lock (_gate)
        {
            contexts = _contexts.ToArray();
        }

        foreach (BrowserContext context in contexts)
        {
            if (!context.IsDefault)
            {
                try
                {
                    await context.DisposeAsync().ConfigureAwait(false);
                }
                catch (CdpException)
                {
                    // The browser may already be gone; closing the rest still matters.
                }
            }
        }

        CdpPage[] pages;
        lock (_gate)
        {
            pages = _pages.ToArray();
        }

        // An attached browser belongs to the caller: disposing our client disconnects and leaves
        // its tabs open, which is what connectOverCDP users expect.
        if (!_attached)
        {
            foreach (CdpPage page in pages)
            {
                try
                {
                    await page.CloseAsync().ConfigureAwait(false);
                }
                catch (CdpException)
                {
                    // The browser may already be gone; closing the rest still matters.
                }
            }
        }

        lock (_gate)
        {
            _pages.Clear();
            _contexts.Clear();
        }
    }
}
