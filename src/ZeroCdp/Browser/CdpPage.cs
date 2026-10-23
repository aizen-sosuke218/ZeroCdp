using System.Text;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

/// <summary>Result of a navigation.</summary>
public readonly struct GotoResult
{
    internal GotoResult(string? frameId, string? loaderId, string url)
    {
        FrameId = frameId;
        LoaderId = loaderId;
        Url = url;
    }

    public string? FrameId { get; }

    /// <summary>Identifies this navigation; changes on every committed document.</summary>
    public string? LoaderId { get; }

    public string Url { get; }
}

/// <summary>
/// A page target: navigation, lifecycle and document inspection over the session bound to it.
/// </summary>
/// <remarks>
/// Navigation and document inspection use the Page and DOM domains, never <c>Runtime.enable</c>.
/// JavaScript runs only in an isolated world created on demand, so no script or execution-context
/// artifact appears in the page's main world — see §4 of the plan for why that matters.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "Playwright spelling WaitForURLAsync coexists with the original WaitForUrlAsync API.")]
public sealed partial class CdpPage : IAsyncDisposable
{

    private TimeSpan _defaultNavigationTimeout = TimeSpan.FromSeconds(30);

    private readonly CdpBrowser _browser;

    private readonly PageState _state = new();

    private readonly InputDispatcher _input;

    private readonly Keyboard _keyboard;

    private readonly Mouse _mouse;
    private readonly Touchscreen _touchscreen;

    private Accessibility? _accessibility;

    private Clock? _clock;

    private Video? _video;

    private ScreencastHub? _screencast;

    private readonly IsolatedWorldRuntime _isolated;

    private readonly NetworkState _network;

    private readonly Dictionary<string, IsolatedWorldRuntime> _frameWorlds = new(StringComparer.Ordinal);

    private readonly Lock _frameWorldsGate = new();

    private readonly Lock _dialogGate = new();

    private readonly Lock _bindingGate = new();

    private readonly Lock _initScriptGate = new();

    private readonly List<string> _initScripts = new();

    private readonly Dictionary<string, Func<string?, ValueTask<string?>>> _bindings = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _bindingResponses = new(StringComparer.Ordinal);

    private readonly Lock _popupGate = new();

    private readonly Lock _downloadGate = new();

    private readonly Lock _fileChooserGate = new();

    private Action<DialogInfo>? _dialogHandlers;

    private Action<CdpPage>? _popupHandlers;

    private Action<Download>? _downloadHandlers;

    private Action<FileChooser>? _fileChooserHandlers;
    private Action? _loadHandlers;
    private Action? _domContentLoadedHandlers;
    private Action<CdpPage>? _closeHandlers;
    private readonly List<Worker> _workers = new();
    private Action<Worker>? _workerHandlers;
    private readonly Lock _pageLifecycleGate = new();
    private int _closeRaised;

    private readonly List<PopupWaiter> _popupWaiters = new();

    private readonly List<DownloadWaiter> _downloadWaiters = new();

    private readonly List<FileChooserWaiter> _fileChooserWaiters = new();

    private readonly Dictionary<string, Download> _activeDownloads = new(StringComparer.Ordinal);

    private int _downloadsConfigured;

    private DialogInfo _currentDialog;

    private TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

    private string? _mainFrameId;

    private int _dialogOpen;

    private int _enabled;

    private Action? _enabledHandlers;

    private int _closed;

    internal CdpPage(CdpBrowser browser, CdpSession session, BrowserContext context)
    {
        _browser = browser;
        _input = new InputDispatcher(session);
        _keyboard = new Keyboard(_input);
        _mouse = new Mouse(_input);
        _touchscreen = new Touchscreen(_input);
        _isolated = new IsolatedWorldRuntime(session);
        _network = new NetworkState(session, context.Routes);
        Session = session;
        Context = context;

        _state.Network = _network;

        // The page owns the session's sink so navigation progress is observable; the caller's sink
        // is chained behind it. A committed document invalidates the isolated world.
        _state.MainFrameNavigated = OnMainFrameNavigated;
        _state.Load = RaiseLoad;
        _state.DomContentLoaded = RaiseDomContentLoaded;
        _state.FrameNavigated = OnFrameNavigated;
        _state.FrameAttached = OnFrameAttached;
        _state.FrameDetached = OnFrameDetached;
        _state.FrameUrlChanged = OnFrameUrlChanged;
        _state.DialogOpening = OnDialogOpening;
        _state.DialogClosed = OnDialogClosed;
        _state.DownloadWillBegin = OnDownloadWillBegin;
        _state.DownloadProgress = OnDownloadProgress;
        _state.FileChooserOpened = OnFileChooserOpened;
        _state.BindingCalled = OnBindingCalled;
        _state.ConsoleMessage = OnWebSocketConsoleMessage;
        session.EventSink = _state;
    }

    public CdpSession Session { get; }

    /// <summary>Trusted keyboard input for this page.</summary>
    public Keyboard Keyboard => _keyboard;

    /// <summary>Trusted mouse input for this page, tracking the pointer position.</summary>
    public Mouse Mouse => _mouse;

    /// <summary>Trusted touch input for this page.</summary>
    public Touchscreen Touchscreen => _touchscreen;

    /// <summary>Accessibility-tree inspection for this page.</summary>
    public Accessibility Accessibility => _accessibility ??= new Accessibility(this);

    /// <summary>The page's video, when the context was created with <see cref="RecordVideoOptions"/>.</summary>
    public Video? Video => _video;

    internal ScreencastHub Screencast => _screencast ??= new ScreencastHub(Session, _state);

    /// <summary>The isolated browser context that owns this page.</summary>
    public BrowserContext Context { get; }

    /// <summary>HTTP client bound to this page's browser context and cookie jar.</summary>
    public APIRequestContext APIRequest => Context.APIRequest;

    public string TargetId => Session.TargetId;

    /// <summary>Target id of the page that opened this popup, when known.</summary>
    public string? OpenerId => Session.OpenerId;

    /// <summary>The page that opened this popup, or null for a directly-created page.</summary>
    public ValueTask<CdpPage?> OpenerAsync()
        => ValueTask.FromResult(_browser.FindPage(OpenerId));

    /// <summary>
    /// How long locator actions and waits poll before failing. Individual calls can override it.
    /// </summary>
    public TimeSpan DefaultTimeout
    {
        get => _defaultTimeout;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            }

            _defaultTimeout = value;
        }
    }

    /// <summary>
    /// When true (the default), a locator action that resolves to more than one element throws a
    /// <see cref="CdpStrictModeException"/> instead of silently using the first match.
    /// </summary>
    public bool StrictSelectors { get; set; } = true;

    /// <summary>How long navigation commands wait before failing.</summary>
    public TimeSpan DefaultNavigationTimeout
    {
        get => _defaultNavigationTimeout;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            }

            _defaultNavigationTimeout = value;
        }
    }

    /// <summary>Playwright-style setter for <see cref="DefaultTimeout"/> in milliseconds.</summary>
    public void SetDefaultTimeout(double timeoutInMilliseconds)
    {
        if (!double.IsFinite(timeoutInMilliseconds) || timeoutInMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutInMilliseconds));
        }

        DefaultTimeout = TimeSpan.FromMilliseconds(timeoutInMilliseconds);
    }

    /// <summary>Playwright-style setter for <see cref="DefaultNavigationTimeout"/> in milliseconds.</summary>
    public void SetDefaultNavigationTimeout(double timeoutInMilliseconds)
    {
        if (!double.IsFinite(timeoutInMilliseconds) || timeoutInMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutInMilliseconds));
        }

        DefaultNavigationTimeout = TimeSpan.FromMilliseconds(timeoutInMilliseconds);
    }

    /// <summary>Id of the main frame, known once <see cref="EnableAsync"/> has run.</summary>
    public string? MainFrameId => _mainFrameId;

    /// <summary>
    /// JavaScript execution environment for this page. It is bound to the main frame and is
    /// recreated after each committed navigation.
    /// </summary>
    public IsolatedWorldRuntime IsolatedWorld => _isolated;

    internal InputDispatcher Input => _input;

    internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    /// <summary>Raised when <see cref="EnableAsync"/> has completed for this page.</summary>
    internal event Action? Enabled
    {
        add
        {
            lock (_pageLifecycleGate)
            {
                _enabledHandlers += value;
            }
        }
        remove
        {
            lock (_pageLifecycleGate)
            {
                _enabledHandlers -= value;
            }
        }
    }

    /// <summary>Events for this page only, chained behind the page's own lifecycle handling.</summary>
    public ICdpEventSink? EventSink
    {
        get => _state.UserSink;
        set => _state.UserSink = value;
    }

    /// <summary>Raised on the reader thread when the main frame reaches the load event.</summary>
    public event Action? Load
    {
        add
        {
            lock (_pageLifecycleGate)
            {
                _loadHandlers += value;
            }
        }
        remove
        {
            lock (_pageLifecycleGate)
            {
                _loadHandlers -= value;
            }
        }
    }

    /// <summary>Raised on the reader thread when the main frame reaches DOMContentLoaded.</summary>
    public event Action? DOMContentLoaded
    {
        add
        {
            lock (_pageLifecycleGate)
            {
                _domContentLoadedHandlers += value;
            }
        }
        remove
        {
            lock (_pageLifecycleGate)
            {
                _domContentLoadedHandlers -= value;
            }
        }
    }

    /// <summary>Raised once when the page target closes or detaches.</summary>
    public event Action<CdpPage>? Close
    {
        add
        {
            lock (_pageLifecycleGate)
            {
                _closeHandlers += value;
            }
        }
        remove
        {
            lock (_pageLifecycleGate)
            {
                _closeHandlers -= value;
            }
        }
    }

    /// <summary>Workers currently attached to this page.</summary>
    public IReadOnlyList<Worker> Workers
    {
        get
        {
            lock (_pageLifecycleGate)
            {
                return _workers.ToArray();
            }
        }
    }

    /// <summary>Raised when a dedicated, shared or service worker attaches to this page.</summary>
    public event Action<Worker>? Worker
    {
        add
        {
            lock (_pageLifecycleGate)
            {
                _workerHandlers += value;
            }
        }
        remove
        {
            lock (_pageLifecycleGate)
            {
                _workerHandlers -= value;
            }
        }
    }

    /// <summary>
    /// Turns on the Page domain and lifecycle reporting, and records the main frame id.
    /// Safe to call repeatedly; <see cref="GotoAsync"/> does it for you.
    /// </summary>
    public async ValueTask EnableAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _enabled) != 0)
        {
            return;
        }

        // Page.enable is not one of the detectable domains: unlike Runtime.enable and Console.enable
        // it does not create observable execution-context artifacts in the page.
        await Session.SendAsync<Unit>("Page.enable"u8, cancellationToken).ConfigureAwait(false);
        await Session
            .SendAsync<Unit, SetLifecycleEventsEnabledParams>(
                "Page.setLifecycleEventsEnabled"u8,
                new SetLifecycleEventsEnabledParams(enabled: true),
                cancellationToken)
            .ConfigureAwait(false);

        // Browser-level auto-attach does not see out-of-process iframes: Chromium requires
        // setAutoAttach on the page session that owns the frame. Flattening keeps the child
        // sessions on the same socket, and waitForDebuggerOnStart lets the router resume them
        // before the iframe runs page script.
        await Session
            .SendAsync<Unit, SetAutoAttachParams>(
                "Target.setAutoAttach"u8,
                new SetAutoAttachParams(autoAttach: true, waitForDebuggerOnStart: true, flatten: true),
                cancellationToken)
            .ConfigureAwait(false);

        FrameTreeResult tree = await Session.SendAsync<FrameTreeResult>("Page.getFrameTree"u8, cancellationToken)
            .ConfigureAwait(false);

        _mainFrameId = tree.MainFrameId;
        _state.SetMainFrame(tree.MainFrameIdUtf8.Span);
        InitializeFrameTree(tree);
        _isolated.SetFrameId(tree.MainFrameId);
        Volatile.Write(ref _enabled, 1);

        Action? enabled;
        lock (_pageLifecycleGate)
        {
            enabled = _enabledHandlers;
        }

        enabled?.Invoke();
    }

    internal async ValueTask ApplyContextOptionsAsync(
        BrowserContextOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Viewport is { } viewport)
        {
            ViewportSize screen = options.Screen ?? viewport;
            await Session
                .SendAsync<Unit, SetDeviceMetricsOverrideParams>(
                    "Emulation.setDeviceMetricsOverride"u8,
                    new SetDeviceMetricsOverrideParams(
                        viewport.Width,
                        viewport.Height,
                        options.DeviceScaleFactor,
                        options.Mobile,
                        screen.Width,
                        screen.Height),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(options.UserAgent))
        {
            await Session
                .SendAsync<Unit, SetUserAgentOverrideParams>(
                    "Emulation.setUserAgentOverride"u8,
                    new SetUserAgentOverrideParams(options.UserAgent, options.Locale),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(options.Locale))
        {
            await Session
                .SendAsync<Unit, SetLocaleOverrideParams>(
                    "Emulation.setLocaleOverride"u8,
                    new SetLocaleOverrideParams(options.Locale),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(options.TimezoneId))
        {
            await Session
                .SendAsync<Unit, SetTimezoneOverrideParams>(
                    "Emulation.setTimezoneOverride"u8,
                    new SetTimezoneOverrideParams(options.TimezoneId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.Geolocation is { } geolocation)
        {
            await Session
                .SendAsync<Unit, SetGeolocationOverrideParams>(
                    "Emulation.setGeolocationOverride"u8,
                    new SetGeolocationOverrideParams(
                        geolocation.Latitude,
                        geolocation.Longitude,
                        geolocation.Accuracy),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        List<EmulatedMediaFeature> mediaFeatures = new();
        if (options.ColorScheme is { } scheme)
        {
            string value = scheme switch
            {
                PreferredColorScheme.Light => "light",
                PreferredColorScheme.Dark => "dark",
                _ => "no-preference",
            };
            mediaFeatures.Add(new EmulatedMediaFeature("prefers-color-scheme", value));
        }

        if (options.ReducedMotion is { } reducedMotion)
        {
            mediaFeatures.Add(new EmulatedMediaFeature(
                "prefers-reduced-motion",
                reducedMotion == ReducedMotion.Reduce ? "reduce" : "no-preference"));
        }

        if (options.ForcedColors is { } forcedColors)
        {
            mediaFeatures.Add(new EmulatedMediaFeature(
                "forced-colors",
                forcedColors == ForcedColors.Active ? "active" : "none"));
        }

        if (options.Contrast is { } contrast)
        {
            string value = contrast switch
            {
                PreferredContrast.More => "more",
                PreferredContrast.Less => "less",
                PreferredContrast.Custom => "custom",
                _ => "no-preference",
            };
            mediaFeatures.Add(new EmulatedMediaFeature("prefers-contrast", value));
        }

        if (mediaFeatures.Count > 0)
        {
            await Session
                .SendAsync<Unit, SetEmulatedMediaParams>(
                    "Emulation.setEmulatedMedia"u8,
                    new SetEmulatedMediaParams(media: null, mediaFeatures),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.HasTouch)
        {
            await Session
                .SendAsync<Unit, SetTouchEmulationEnabledParams>(
                    "Emulation.setTouchEmulationEnabled"u8,
                    new SetTouchEmulationEnabledParams(true, 5),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.Offline)
        {
            await Session
                .SendAsync<Unit, EmulateNetworkConditionsParams>(
                    "Network.emulateNetworkConditions"u8,
                    new EmulateNetworkConditionsParams(offline: true),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!options.AcceptDownloads)
        {
            await Session
                .SendAsync<Unit, SetDownloadBehaviorParams>(
                    "Page.setDownloadBehavior"u8,
                    new SetDownloadBehaviorParams("deny", string.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.ServiceWorkers == ServiceWorkerPolicy.Block)
        {
            await Session
                .SendAsync<Unit, SetBypassServiceWorkerParams>(
                    "Network.setBypassServiceWorker"u8,
                    new SetBypassServiceWorkerParams(true),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.HttpCredentials is not null)
        {
            await _network
                .SetHttpCredentialsAsync(options.HttpCredentials, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!options.JavaScriptEnabled)
        {
            await Session
                .SendAsync<Unit, SetScriptExecutionDisabledParams>(
                    "Emulation.setScriptExecutionDisabled"u8,
                    new SetScriptExecutionDisabledParams(true),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.BypassCsp)
        {
            await Session
                .SendAsync<Unit, SetBypassCspParams>(
                    "Page.setBypassCSP"u8,
                    new SetBypassCspParams(true),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.ExtraHttpHeaders.Count > 0)
        {
            await Session
                .SendAsync<Unit, SetExtraHttpHeadersParams>(
                    "Network.setExtraHTTPHeaders"u8,
                    new SetExtraHttpHeadersParams(options.ExtraHttpHeaders),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reloads the current document.</summary>

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    private void RaiseLoad()
    {
        Action? handlers;
        lock (_pageLifecycleGate)
        {
            handlers = _loadHandlers;
        }

        handlers?.Invoke();
    }

    private void RaiseDomContentLoaded()
    {
        Action? handlers;
        lock (_pageLifecycleGate)
        {
            handlers = _domContentLoadedHandlers;
        }

        handlers?.Invoke();
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closeRaised, 1) != 0)
        {
            return;
        }

        Action<CdpPage>? handlers;
        lock (_pageLifecycleGate)
        {
            handlers = _closeHandlers;
        }

        handlers?.Invoke(this);
    }

    internal void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _network.Dispose();
            _video?.StopInBackground();
            _screencast?.StopInBackground();
        }

        RaiseClosed();
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _network.Dispose();
        await StopVideoAsync().ConfigureAwait(false);
        await StopScreencastAsync().ConfigureAwait(false);

        try
        {
            await _browser.ClosePageAsync(this, cancellationToken).ConfigureAwait(false);
        }
        catch (CdpConnectionClosedException)
        {
            // The browser is already gone, so the page is closed by definition. Cleanup should not
            // throw just because the process died first.
        }
        finally
        {
            RaiseClosed();
        }
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
