using System.Text.Json;
using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>Options for closing an intercepted WebSocket.</summary>
public sealed class WebSocketRouteCloseOptions
{
    /// <summary>RFC 6455 close code. Defaults to 1000 (normal closure).</summary>
    public int Code { get; init; } = 1000;

    /// <summary>Optional human-readable close reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// An intercepted WebSocket. A route can proxy frames to a real server, answer them locally, or
/// do both. Instances are created by <see cref="CdpPage.RouteWebSocketAsync(string,Func{WebSocketRoute,ValueTask},CancellationToken)"/>
/// and <see cref="BrowserContext.RouteWebSocketAsync(string,Func{WebSocketRoute,ValueTask},CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Chromium's DevTools protocol has no frame-level WebSocket interception point. ZeroCDP therefore
/// installs a small main-world <c>WebSocket</c> wrapper when the first route is registered. This is
/// an opt-in surface: pages can observe the wrapper and a strict Content-Security-Policy can block
/// the injected script. Without a route no wrapper is installed and no page-visible surface exists.
/// </remarks>
public sealed class WebSocketRoute
{
    private readonly CdpPage _page;
    private readonly long _id;
    private readonly Lock _gate = new();
    private Func<string, ValueTask>? _clientText;
    private Func<byte[], ValueTask>? _clientBinary;
    private Func<string, ValueTask>? _serverText;
    private Func<byte[], ValueTask>? _serverBinary;
    private int _connected;
    private int _closed;
    private int _openNotified;

    internal WebSocketRoute(CdpPage page, long id, string url)
    {
        _page = page;
        _id = id;
        Url = url;
    }

    internal long Id => _id;

    /// <summary>URL supplied to the page's <c>WebSocket</c> constructor.</summary>
    public string Url { get; }

    /// <summary>Whether this route has been connected to the real server.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    /// <summary>Whether this route has been closed or the page navigated away.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>True while client frames are automatically forwarded to the real server.</summary>
    internal bool AutoProxy { get; set; }

    /// <summary>
    /// Connects this route to the real WebSocket endpoint. Until this is called a matched route is
    /// a local mock and no network connection is made.
    /// </summary>
    public async ValueTask ConnectToServerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (Interlocked.Exchange(ref _connected, 1) != 0)
        {
            return;
        }

        AutoProxy = true;
        await _page
            .SendWebSocketControlAsync(_id, "connect", null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Registers a handler for text frames sent by the page.</summary>
    public void OnMessage(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _clientText = value =>
            {
                handler(value);
                return ValueTask.CompletedTask;
            };
        }
    }

    /// <summary>Registers an asynchronous handler for text frames sent by the page.</summary>
    public void OnMessage(Func<string, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _clientText = handler;
        }
    }

    /// <summary>Registers a handler for binary frames sent by the page.</summary>
    public void OnMessage(Action<byte[]> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _clientBinary = value =>
            {
                handler(value);
                return ValueTask.CompletedTask;
            };
        }
    }

    /// <summary>Registers an asynchronous handler for binary frames sent by the page.</summary>
    public void OnMessage(Func<byte[], ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _clientBinary = handler;
        }
    }

    /// <summary>Registers a handler for text frames received from the real server.</summary>
    public void OnServerMessage(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _serverText = value =>
            {
                handler(value);
                return ValueTask.CompletedTask;
            };
        }
    }

    /// <summary>Registers an asynchronous handler for text frames received from the real server.</summary>
    public void OnServerMessage(Func<string, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _serverText = handler;
        }
    }

    /// <summary>Registers a handler for binary frames received from the real server.</summary>
    public void OnServerMessage(Action<byte[]> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _serverBinary = value =>
            {
                handler(value);
                return ValueTask.CompletedTask;
            };
        }
    }

    /// <summary>Registers an asynchronous handler for binary frames received from the real server.</summary>
    public void OnServerMessage(Func<byte[], ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _serverBinary = handler;
        }
    }

    /// <summary>Sends a text frame to the page.</summary>
    public ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendToPageAsync(message, binary: false, cancellationToken);
    }

    /// <summary>Sends a binary frame to the page.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => SendToPageAsync(Convert.ToBase64String(data.Span), binary: true, cancellationToken);

    /// <summary>Sends a text frame to the real server, when connected.</summary>
    public ValueTask SendToServerAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendToServerCoreAsync(message, binary: false, cancellationToken);
    }

    /// <summary>Sends a binary frame to the real server, when connected.</summary>
    public ValueTask SendToServerAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => SendToServerCoreAsync(Convert.ToBase64String(data.Span), binary: true, cancellationToken);

    /// <summary>Closes the page's WebSocket and, when connected, the real server socket.</summary>
    public async ValueTask CloseAsync(
        WebSocketRouteCloseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WebSocketRouteCloseOptions();
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        string payload = "{\"code\":" + options.Code.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"reason\":" + JsText.Quote(options.Reason ?? string.Empty) + "}";
        try
        {
            await _page
                .SendWebSocketControlAsync(_id, "close", payload, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // The page may already be gone.
        }
    }

    internal async ValueTask NotifyAcceptedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _openNotified, 1) != 0)
        {
            return;
        }

        await _page
            .SendWebSocketControlAsync(_id, "accept", null, cancellationToken)
            .ConfigureAwait(false);
    }

    internal ValueTask HandleClientFrameAsync(string data, bool binary, CancellationToken cancellationToken)
    {
        Func<string, ValueTask>? text = null;
        Func<byte[], ValueTask>? bytes = null;
        lock (_gate)
        {
            text = _clientText;
            bytes = _clientBinary;
        }

        if (binary)
        {
            if (bytes is not null)
            {
                return bytes(Convert.FromBase64String(data));
            }
        }
        else if (text is not null)
        {
            return text(data);
        }

        return AutoProxy
            ? SendToServerCoreAsync(data, binary, cancellationToken)
            : ValueTask.CompletedTask;
    }

    internal ValueTask HandleServerFrameAsync(string data, bool binary, CancellationToken cancellationToken)
    {
        Func<string, ValueTask>? text = null;
        Func<byte[], ValueTask>? bytes = null;
        lock (_gate)
        {
            text = _serverText;
            bytes = _serverBinary;
        }

        if (binary)
        {
            if (bytes is not null)
            {
                return bytes(Convert.FromBase64String(data));
            }
        }
        else if (text is not null)
        {
            return text(data);
        }

        return AutoProxy
            ? SendToPageAsync(data, binary, cancellationToken)
            : ValueTask.CompletedTask;
    }

    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);

    internal void MarkConnected() => Interlocked.Exchange(ref _connected, 1);

    private ValueTask SendToPageAsync(string data, bool binary, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        string payload = "{\"data\":" + JsText.Quote(data)
            + ",\"binary\":" + (binary ? "true" : "false") + "}";
        return _page.SendWebSocketControlAsync(_id, "deliver", payload, cancellationToken);
    }

    private ValueTask SendToServerCoreAsync(string data, bool binary, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        string payload = "{\"data\":" + JsText.Quote(data)
            + ",\"binary\":" + (binary ? "true" : "false") + "}";
        return _page.SendWebSocketControlAsync(_id, "toServer", payload, cancellationToken);
    }
}
