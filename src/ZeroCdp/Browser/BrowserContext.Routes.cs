namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    /// <summary>
    /// Intercepts requests in every page of this context, including pages created later. Page
    /// routes take priority over context routes.
    /// </summary>
    public async ValueTask RouteAsync(
        string urlPattern,
        Func<Route, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(urlPattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        _routes.Add(urlPattern, handler);
        try
        {
            foreach (CdpPage page in Pages)
            {
                if (!page.IsClosed)
                {
                    await page.EnsureRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            _routes.Remove(urlPattern);
            await RefreshRoutesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes context routes matching a pattern, or all context routes.</summary>
    public async ValueTask UnrouteAsync(
        string? urlPattern = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _routes.Remove(urlPattern);
        await RefreshRoutesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replays responses from a HAR 1.2 archive for requests in this context. Requests are matched
    /// by method and exact URL. By default a missing entry aborts the request; use
    /// <see cref="HarReplayOptions.NotFound"/> to fall through to the network instead.
    /// </summary>
    public async ValueTask RouteFromHARAsync(
        string harPath,
        HarReplayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(harPath);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        HarPlayer player = await HarPlayer
            .LoadAsync(harPath, options, cancellationToken)
            .ConfigureAwait(false);

        await RouteAsync(
                "**/*",
                route => player.HandleAsync(route, CancellationToken.None),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask RefreshRoutesAsync(CancellationToken cancellationToken)
    {
        foreach (CdpPage page in Pages)
        {
            if (page.IsClosed)
            {
                continue;
            }

            try
            {
                await page.RefreshRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // A page can close between the snapshot and the command.
            }
        }
    }

    /// <summary>Returns this context's cookies.</summary>
}
