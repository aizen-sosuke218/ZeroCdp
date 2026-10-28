using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>
/// An isolated browser context. Incognito contexts have their own cookie jar, storage and cache,
/// which makes them suitable for separate accounts or test cases in one browser process.
/// </summary>
/// <remarks>
/// The default context is created by the browser at launch and is exposed as
/// <see cref="CdpBrowser.DefaultContext"/>. Closing an incognito context closes all of its pages
/// and asks Chromium to discard its storage.
/// </remarks>
public sealed partial class BrowserContext : IAsyncDisposable
{
    private readonly CdpBrowser _browser;
    private readonly List<CdpPage> _pages = new();
    private readonly RouteRegistry _routes = new();
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);
    private BrowserContextTracing? _tracing;

    private readonly Lock _gate = new();
    private readonly Lock _pageEventGate = new();
    private readonly Lock _serviceWorkerEventGate = new();
    private readonly Lock _settingsGate = new();
    private readonly HarRecorder? _harRecorder;
    private readonly List<string> _initScripts = new();
    private readonly List<ServiceWorker> _serviceWorkers = new();
    private TimeSpan _defaultTimeout = DefaultWaitTimeout;
    private TimeSpan _defaultNavigationTimeout = DefaultWaitTimeout;
    private bool _strictSelectors = true;
    private readonly Action<PageRequest> _forwardRequest;
    private readonly Action<PageResponse> _forwardResponse;
    private readonly Action<RequestFailure> _forwardFailure;
    private readonly Action<WebSocketInfo> _forwardWebSocket;
    private readonly Action<PageResponse> _forwardFinished;
    private Action<CdpPage>? _pageOpened;
    private Action<CdpPage>? _pageAdded;
    private Action<CdpPage>? _pageClosed;
    private Action<ServiceWorker>? _serviceWorker;
    private Action<PageRequest>? _requestStarted;
    private Action<PageResponse>? _responseReceived;
    private Action<RequestFailure>? _requestFailed;
    private Action<WebSocketInfo>? _webSocketCreated;
    private Action<PageResponse>? _requestFinished;
    private int _disposed;

    internal BrowserContext(CdpBrowser browser, string? browserContextId, BrowserContextOptions? options)
    {
        _browser = browser;
        BrowserContextId = browserContextId;
        Options = options;
        _strictSelectors = options?.StrictSelectors ?? true;
        _harRecorder = string.IsNullOrEmpty(options?.RecordHarPath)
            ? null
            : new HarRecorder(options.RecordHarPath);
        _forwardRequest = OnPageRequest;
        _forwardResponse = OnPageResponse;
        _forwardFailure = OnPageFailure;
        _forwardWebSocket = OnPageWebSocket;
        _forwardFinished = OnPageFinished;
    }

    public CdpBrowser Browser => _browser;

    /// <summary>Chromium's context id; null for the default context.</summary>
    public string? BrowserContextId { get; }

    public bool IsDefault => BrowserContextId is null;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Emulation options for this context, or null for the default context.</summary>
    public BrowserContextOptions? Options { get; }

    /// <summary>Playwright-style trace recording for this context.</summary>
    public BrowserContextTracing Tracing => _tracing ??= new BrowserContextTracing(this);

    internal RouteRegistry Routes => _routes;

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

    /// <summary>Service workers currently registered in this context.</summary>
    public IReadOnlyList<ServiceWorker> ServiceWorkers
    {
        get
        {
            lock (_gate)
            {
                return _serviceWorkers.ToArray();
            }
        }
    }

    /// <summary>Raised on the reader thread when a service worker attaches.</summary>
    public event Action<ServiceWorker>? ServiceWorker
    {
        add
        {
            lock (_serviceWorkerEventGate)
            {
                _serviceWorker += value;
            }
        }
        remove
        {
            lock (_serviceWorkerEventGate)
            {
                _serviceWorker -= value;
            }
        }
    }

    /// <summary>Default timeout applied to locators and waits in this context.</summary>
    public TimeSpan DefaultTimeout => _defaultTimeout;

    /// <summary>Default navigation timeout applied to pages in this context.</summary>
    public TimeSpan DefaultNavigationTimeout => _defaultNavigationTimeout;

    /// <summary>Whether locator actions in this context use strict mode. Defaults to true.</summary>
    public bool StrictSelectors => _strictSelectors;

    /// <summary>Playwright-style context default timeout setter, in milliseconds.</summary>
    public void SetDefaultTimeout(double timeoutInMilliseconds)
    {
        if (!double.IsFinite(timeoutInMilliseconds) || timeoutInMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutInMilliseconds));
        }

        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutInMilliseconds);
        lock (_settingsGate)
        {
            _defaultTimeout = timeout;
        }

        foreach (CdpPage page in SnapshotPages())
        {
            page.DefaultTimeout = timeout;
        }
    }

    /// <summary>Playwright-style context navigation timeout setter, in milliseconds.</summary>
    public void SetDefaultNavigationTimeout(double timeoutInMilliseconds)
    {
        if (!double.IsFinite(timeoutInMilliseconds) || timeoutInMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutInMilliseconds));
        }

        TimeSpan timeout = TimeSpan.FromMilliseconds(timeoutInMilliseconds);
        lock (_settingsGate)
        {
            _defaultNavigationTimeout = timeout;
        }

        foreach (CdpPage page in SnapshotPages())
        {
            page.DefaultNavigationTimeout = timeout;
        }
    }

    /// <summary>
    /// Adds a script that runs after every document in every page of this context. The script is
    /// injected by the page's document transformer, so no init-script CDP command is emitted.
    /// </summary>
    public async ValueTask AddInitScriptAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        lock (_settingsGate)
        {
            _initScripts.Add(script);
        }

        foreach (CdpPage page in SnapshotPages())
        {
            await page.AddInitScriptAsync(script, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask ApplyToPageAsync(CdpPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        TimeSpan defaultTimeout;
        TimeSpan navigationTimeout;
        bool strict;
        string[] scripts;
        lock (_settingsGate)
        {
            defaultTimeout = _defaultTimeout;
            navigationTimeout = _defaultNavigationTimeout;
            strict = _strictSelectors;
            scripts = _initScripts.ToArray();
        }

        page.DefaultTimeout = defaultTimeout;
        page.DefaultNavigationTimeout = navigationTimeout;
        page.StrictSelectors = strict;

        foreach (string script in scripts)
        {
            await page.AddInitScriptAsync(script, cancellationToken).ConfigureAwait(false);
        }

        if (HasWebSocketRoutes)
        {
            await page.EnsureWebSocketInterceptorAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Raised when a page registers in this context: pages created through
    /// <see cref="NewPageAsync(string,CancellationToken)"/> and pages opened by a page, for example
    /// <c>window.open</c>. The page is present in <see cref="Pages"/> by the time the handler runs.
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

    /// <summary>
    /// Raised as soon as any page is registered in this context, including pages created with
    /// <see cref="NewPageAsync(string,CancellationToken)"/>. Internal recorders use this to attach
    /// before the page is enabled.
    /// </summary>
    internal event Action<CdpPage>? PageAdded
    {
        add
        {
            lock (_pageEventGate)
            {
                _pageAdded += value;
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _pageAdded -= value;
            }
        }
    }

    /// <summary>Raised when one of this context's page targets detaches or closes.</summary>
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

    /// <summary>Opens a page in this context.</summary>
    public ValueTask<CdpPage> NewPageAsync(string url = "about:blank", CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _browser.CreatePageAsync(this, url, cancellationToken);
    }

    /// <summary>Opens a page with per-page emulation options.</summary>
    public async ValueTask<CdpPage> NewPageAsync(
        BrowserNewPageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        CdpPage page = await NewPageAsync("about:blank", cancellationToken).ConfigureAwait(false);
        try
        {
            await page.ApplyContextOptionsAsync(options.ToContextOptions(), cancellationToken)
                .ConfigureAwait(false);
            page.StrictSelectors = options.StrictSelectors;
            return page;
        }
        catch
        {
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Attaches an independent CDP session to <paramref name="page"/>. The session receives only
    /// its own events and does not disturb the page's internal session.
    /// </summary>
    public async ValueTask<CdpSession> NewCDPSessionAsync(
        CdpPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!ReferenceEquals(page.Context, this))
        {
            throw new ArgumentException("The page belongs to a different browser context.", nameof(page));
        }

        ObjectDisposedException.ThrowIf(page.IsClosed, page);

        TargetAttached attached = await _browser.Connection
            .SendAsync<TargetAttached, AttachToTargetParams>(
                "Target.attachToTarget"u8,
                new AttachToTargetParams(page.TargetId, flatten: true),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(attached.SessionId))
        {
            throw new CdpException("The browser did not return a CDP session id.");
        }

        CdpSession session = await _browser
            .WaitForAttachedSessionAsync(page.TargetId, attached.SessionId, cancellationToken)
            .ConfigureAwait(false);
        return session;
    }

    /// <summary>Opens a page at <paramref name="url"/> with per-page emulation options.</summary>
    public async ValueTask<CdpPage> NewPageAsync(
        string url,
        BrowserNewPageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        CdpPage page = await NewPageAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, WaitUntil.Load, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return page;
        }
        catch
        {
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal void AddPage(CdpPage page)
    {
        Action<CdpPage>? added;
        lock (_pageEventGate)
        {
            lock (_gate)
            {
                _pages.Add(page);
            }

            if (_requestStarted is not null)
            {
                page.Request += _forwardRequest;
            }

            if (_responseReceived is not null)
            {
                page.Response += _forwardResponse;
            }

            if (_requestFailed is not null)
            {
                page.RequestFailed += _forwardFailure;
            }

            if (_webSocketCreated is not null)
            {
                page.WebSocketCreated += _forwardWebSocket;
            }

            if (_requestFinished is not null)
            {
                page.RequestFinished += _forwardFinished;
            }

            _harRecorder?.Attach(page);
            added = _pageAdded;
        }

        added?.Invoke(page);
    }

    internal void RemovePage(CdpPage page)
    {
        lock (_pageEventGate)
        {
            page.Request -= _forwardRequest;
            page.Response -= _forwardResponse;
            page.RequestFailed -= _forwardFailure;
            page.WebSocketCreated -= _forwardWebSocket;
            page.RequestFinished -= _forwardFinished;
            _harRecorder?.Detach(page);

            lock (_gate)
            {
                _pages.Remove(page);
            }
        }
    }

    private void OnPageRequest(PageRequest request) => _requestStarted?.Invoke(request);

    private void OnPageResponse(PageResponse response) => _responseReceived?.Invoke(response);

    private void OnPageFailure(RequestFailure failure) => _requestFailed?.Invoke(failure);

    private void OnPageWebSocket(WebSocketInfo webSocket) => _webSocketCreated?.Invoke(webSocket);

    private void OnPageFinished(PageResponse response) => _requestFinished?.Invoke(response);

    internal void RegisterServiceWorker(CdpSession session)
    {
        ServiceWorker worker = new(this, session);
        Action<ServiceWorker>? handlers;
        lock (_gate)
        {
            foreach (ServiceWorker existing in _serviceWorkers)
            {
                if (existing.TargetId.Equals(worker.TargetId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _serviceWorkers.Add(worker);
        }

        lock (_serviceWorkerEventGate)
        {
            handlers = _serviceWorker;
        }

        handlers?.Invoke(worker);
    }

    internal void RemoveServiceWorker(CdpSession session)
    {
        lock (_gate)
        {
            for (int i = _serviceWorkers.Count - 1; i >= 0; i--)
            {
                if (_serviceWorkers[i].TargetId.Equals(session.TargetId, StringComparison.Ordinal))
                {
                    _serviceWorkers[i].MarkClosed();
                    _serviceWorkers.RemoveAt(i);
                    break;
                }
            }
        }
    }

    internal void RaisePageOpened(CdpPage page)
    {
        Action<CdpPage>? handlers;
        lock (_pageEventGate)
        {
            handlers = _pageOpened;
        }

        handlers?.Invoke(page);
    }

    internal void RaisePageClosed(CdpPage page)
    {
        Action<CdpPage>? handlers;
        lock (_pageEventGate)
        {
            handlers = _pageClosed;
        }

        handlers?.Invoke(page);
    }

    internal CdpPage[] SnapshotPages()
    {
        lock (_gate)
        {
            return _pages.ToArray();
        }
    }

    internal void ClearPages()
    {
        lock (_pageEventGate)
        {
            foreach (CdpPage page in Pages)
            {
                page.Request -= _forwardRequest;
                page.Response -= _forwardResponse;
                page.RequestFailed -= _forwardFailure;
                page.WebSocketCreated -= _forwardWebSocket;
                page.RequestFinished -= _forwardFinished;
            }

            lock (_gate)
            {
                _pages.Clear();
            }
        }
    }

    internal bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    internal async ValueTask WriteHarAsync(CancellationToken cancellationToken)
    {
        if (_harRecorder is not null)
        {
            await _harRecorder.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Playwright-style context close alias.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        => _browser.CloseContextAsync(this, cancellationToken);

    public ValueTask DisposeAsync() => _browser.CloseContextAsync(this, CancellationToken.None);
}
