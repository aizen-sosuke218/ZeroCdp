namespace ZeroCdp.Browser;

/// <summary>A URL glob paired with the handler that should receive matching requests.</summary>
internal sealed class RouteRegistration
{
    public RouteRegistration(string pattern, Func<Route, ValueTask> handler)
    {
        Pattern = pattern;
        Handler = handler;
    }

    public string Pattern { get; }

    public Func<Route, ValueTask> Handler { get; }
}

/// <summary>
/// Shared route list for a browser context. Page routes are consulted first; when no page route
/// matches, the page falls back to this registry.
/// </summary>
internal sealed class RouteRegistry
{
    private readonly Lock _gate = new();
    private readonly List<RouteRegistration> _routes = new();

    public bool HasRoutes
    {
        get
        {
            lock (_gate)
            {
                return _routes.Count > 0;
            }
        }
    }

    public void Add(string pattern, Func<Route, ValueTask> handler)
    {
        lock (_gate)
        {
            _routes.Add(new RouteRegistration(pattern, handler));
        }
    }

    public void Remove(string? pattern)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                _routes.Clear();
            }
            else
            {
                _routes.RemoveAll(route => route.Pattern.Equals(pattern, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// Every matching handler, newest first, so <see cref="Route.FallbackAsync"/> can walk the
    /// chain exactly like route fallback does in Playwright.
    /// </summary>
    public Func<Route, ValueTask>[]? FindAll(string url)
    {
        List<Func<Route, ValueTask>>? matches = null;
        lock (_gate)
        {
            for (int i = _routes.Count - 1; i >= 0; i--)
            {
                RouteRegistration registration = _routes[i];
                if (UrlGlob.IsMatch(registration.Pattern, url))
                {
                    (matches ??= new List<Func<Route, ValueTask>>()).Add(registration.Handler);
                }
            }
        }

        return matches?.ToArray();
    }
}
