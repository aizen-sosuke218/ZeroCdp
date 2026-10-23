namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    private readonly Lock _webSocketRouteGate = new();
    private readonly List<WebSocketRouteRegistration> _webSocketRoutes = new();

    internal bool HasWebSocketRoutes
    {
        get
        {
            lock (_webSocketRouteGate)
            {
                return _webSocketRoutes.Count > 0;
            }
        }
    }

    /// <summary>
    /// Intercepts WebSockets created by any page in this context whose URL matches a glob. The
    /// route also applies to pages opened later in the context. See
    /// <see cref="CdpPage.RouteWebSocketAsync(string,Func{WebSocketRoute,ValueTask},CancellationToken)"/>
    /// for the interception mechanism and its opt-in trade-offs.
    /// </summary>
    public async ValueTask RouteWebSocketAsync(
        string urlPattern,
        Func<WebSocketRoute, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(urlPattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        lock (_webSocketRouteGate)
        {
            _webSocketRoutes.Add(new WebSocketRouteRegistration(urlPattern, handler));
        }

        foreach (CdpPage page in SnapshotPages())
        {
            try
            {
                await page.EnsureWebSocketInterceptorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
            {
                // A page can close while routes are being installed.
            }
        }
    }

    /// <summary>Intercepts WebSockets with a synchronous handler.</summary>
    public ValueTask RouteWebSocketAsync(
        string urlPattern,
        Action<WebSocketRoute> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RouteWebSocketAsync(
            urlPattern,
            route =>
            {
                handler(route);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Removes context WebSocket routes for a URL glob, or every route when null.</summary>
    public ValueTask UnrouteWebSocketAsync(
        string? urlPattern = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        lock (_webSocketRouteGate)
        {
            if (urlPattern is null)
            {
                _webSocketRoutes.Clear();
            }
            else
            {
                _webSocketRoutes.RemoveAll(route =>
                    route.Pattern.Equals(urlPattern, StringComparison.Ordinal));
            }
        }

        return ValueTask.CompletedTask;
    }

    internal bool TryResolveWebSocketRoute(
        string url,
        out Func<WebSocketRoute, ValueTask>? handler)
    {
        handler = null;
        lock (_webSocketRouteGate)
        {
            for (int i = _webSocketRoutes.Count - 1; i >= 0; i--)
            {
                WebSocketRouteRegistration route = _webSocketRoutes[i];
                if (UrlGlob.IsMatch(route.Pattern, url))
                {
                    handler = route.Handler;
                    return true;
                }
            }
        }

        return false;
    }
}
