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
    /// Raised on the reader thread for every request observed by the Network domain, after
    /// <see cref="EnableNetworkAsync"/> or a network waiter has turned observation on.
    /// </summary>
    public event Action<PageRequest>? Request
    {
        add
        {
            _network.RequestStarted += value;
            ScheduleNetworkEnable();
        }
        remove => _network.RequestStarted -= value;
    }

    /// <summary>Raised on the reader thread for every response observed by the Network domain.</summary>
    public event Action<PageResponse>? Response
    {
        add
        {
            _network.ResponseReceived += value;
            ScheduleNetworkEnable();
        }
        remove => _network.ResponseReceived -= value;
    }

    /// <summary>Raised on the reader thread when a request fails before producing a response.</summary>
    public event Action<RequestFailure>? RequestFailed
    {
        add
        {
            _network.RequestFailed += value;
            ScheduleNetworkEnable();
        }
        remove => _network.RequestFailed -= value;
    }

    /// <summary>
    /// Raised on the reader thread when the page opens a WebSocket. Subscribing enables
    /// Network-domain observation, which is opt-in and does not inject page-visible script.
    /// </summary>
    public event Action<WebSocketInfo>? WebSocketCreated
    {
        add
        {
            _network.WebSocketCreated += value;
            ScheduleNetworkEnable();
        }
        remove => _network.WebSocketCreated -= value;
    }

    /// <summary>Playwright-style alias for <see cref="WebSocketCreated"/>.</summary>
    public event Action<WebSocketInfo>? WebSocket
    {
        add => WebSocketCreated += value;
        remove => WebSocketCreated -= value;
    }

    /// <summary>Enables Network-domain observation. Route interception does not require this.</summary>
    public async ValueTask EnableNetworkAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await _network.EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Intercepts requests whose URL matches a glob pattern. The most recently registered matching
    /// route wins. Handlers run on the thread pool and may await; a request stays paused until the
    /// route is continued, fulfilled or aborted. If a handler returns without acting, the request
    /// is continued automatically so a forgotten route cannot hang the page.
    /// </summary>
    public async ValueTask RouteAsync(
        string urlPattern,
        Func<Route, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await _network.RouteAsync(urlPattern, handler, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes routes for one pattern, or every route when <paramref name="urlPattern"/> is null.</summary>
    public async ValueTask UnrouteAsync(
        string? urlPattern = null,
        CancellationToken cancellationToken = default)
    {
        await _network.UnrouteAsync(urlPattern, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays responses from a HAR 1.2 archive for requests made by this page. Requests are
    /// matched by method and exact URL. Missing entries abort by default; set
    /// <see cref="HarReplayOptions.NotFound"/> to fall through to the network.
    /// </summary>
    public async ValueTask RouteFromHARAsync(
        string harPath,
        HarReplayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(harPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        HarPlayer player = await HarPlayer
            .LoadAsync(harPath, options, cancellationToken)
            .ConfigureAwait(false);

        await RouteAsync(
                "**/*",
                route => player.HandleAsync(route, CancellationToken.None),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal ValueTask EnsureRouteInterceptionAsync(CancellationToken cancellationToken)
        => _network.EnsureRouteInterceptionAsync(cancellationToken);

    internal ValueTask RefreshRouteInterceptionAsync(CancellationToken cancellationToken)
        => _network.RefreshRouteInterceptionAsync(cancellationToken);

    /// <summary>Waits for the next request matching a URL glob.</summary>
    public ValueTask<PageRequest> WaitForRequestAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return _network.WaitForRequestAsync(urlPattern, timeout ?? DefaultTimeout, cancellationToken);
    }

    /// <summary>Waits for the next response matching a URL glob.</summary>
    public ValueTask<PageResponse> WaitForResponseAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return _network.WaitForResponseAsync(urlPattern, timeout ?? DefaultTimeout, cancellationToken);
    }

    /// <summary>Waits for the next WebSocket matching a URL glob.</summary>
    public ValueTask<WebSocketInfo> WaitForWebSocketAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return _network.WaitForWebSocketAsync(urlPattern, timeout ?? DefaultTimeout, cancellationToken);
    }

    /// <summary>Reads the body for a response observed by the Network domain.</summary>
    public async ValueTask<NetworkBody> GetResponseBodyAsync(
        PageResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(response.RequestId);
        ResponseBodyResult result = await Session
            .SendAsync<ResponseBodyResult, GetResponseBodyParams>(
                "Network.getResponseBody"u8,
                new GetResponseBodyParams(response.RequestId),
                cancellationToken)
            .ConfigureAwait(false);

        byte[] data = result.Base64Encoded
            ? Convert.FromBase64String(result.Body)
            : System.Text.Encoding.UTF8.GetBytes(result.Body);
        return new NetworkBody(data, result.Base64Encoded);
    }

    /// <summary>Emulates offline mode for this page's network stack.</summary>
    public async ValueTask SetOfflineAsync(bool offline, CancellationToken cancellationToken = default)
        => await Session.SendAsync<Unit, EmulateNetworkConditionsParams>(
            "Network.emulateNetworkConditions"u8,
            new EmulateNetworkConditionsParams(offline),
            cancellationToken).ConfigureAwait(false);

    /// <summary>Disables the HTTP cache for this page.</summary>
    public async ValueTask SetCacheDisabledAsync(bool disabled, CancellationToken cancellationToken = default)
        => await Session.SendAsync<Unit, SetCacheDisabledParams>(
            "Network.setCacheDisabled"u8,
            new SetCacheDisabledParams(disabled),
            cancellationToken).ConfigureAwait(false);

    /// <summary>Replaces the extra headers sent by this page.</summary>
    public async ValueTask SetExtraHTTPHeadersAsync(
        IReadOnlyList<CdpHeader> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        await Session.SendAsync<Unit, SetExtraHttpHeadersParams>(
            "Network.setExtraHTTPHeaders"u8,
            new SetExtraHttpHeadersParams(headers),
            cancellationToken).ConfigureAwait(false);
    }

    private void ScheduleNetworkEnable()
    {
        _ = EnableNetworkQuietlyAsync();
    }

    private async Task EnableNetworkQuietlyAsync()
    {
        try
        {
            await _network.EnsureNetworkEnabledAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // Subscribing to events on a page that closes immediately is harmless.
        }
    }

    /// <summary>Id of the main frame, known once <see cref="EnableAsync"/> has run.</summary>
}
