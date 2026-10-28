using System.Text;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Security;

namespace ZeroCdp.Browser;

public sealed partial class CdpBrowser
{
    private void OnSessionAttached(CdpSession session)
    {
        if (session.TargetType.Equals("service_worker", StringComparison.Ordinal))
        {
            BrowserContext? serviceWorkerContext = ResolveContext(session.BrowserContextId);
            if (serviceWorkerContext is not null)
            {
                lock (_gate)
                {
                    _serviceWorkerContexts[session.SessionId] = serviceWorkerContext;
                }

                serviceWorkerContext.RegisterServiceWorker(session);
            }
        }

        if (!string.IsNullOrEmpty(session.ParentSessionId))
        {
            CdpPage? owner = null;
            lock (_gate)
            {
                _sessionOwners.TryGetValue(session.ParentSessionId, out owner);
                if (owner is not null)
                {
                    _sessionOwners[session.SessionId] = owner;
                }
            }

            owner?.OnChildSessionAttached(session);
            if (owner is null
                && (session.TargetType.Equals("worker", StringComparison.Ordinal)
                    || session.TargetType.Equals("shared_worker", StringComparison.Ordinal)
                    || session.TargetType.Equals("service_worker", StringComparison.Ordinal)))
            {
                FindPage(session.OpenerId)?.OnChildSessionAttached(session);
            }
        }

        if (session.TargetType.Equals("page", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(session.OpenerId))
        {
            try
            {
                RegisterOpenedPage(session);
            }
            catch (Exception ex) when (ex is CdpException or ArgumentException or InvalidOperationException)
            {
                // A target can disappear between attach and registration. The session itself is
                // still visible through CdpBrowser.Sessions, so nothing else is lost.
            }
        }

        _sessionAttached?.Invoke(session);
    }

    /// <summary>
    /// Creates a CDP session addressed to the browser endpoint. Use it for browser-level commands
    /// such as <c>Browser.*</c> and <c>Target.*</c>; it does not attach to a page target.
    /// </summary>
    public ValueTask<CdpSession> NewBrowserCDPSessionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CdpSession.CreateBrowserSession(_connection));
    }

    internal async ValueTask<CdpSession> WaitForAttachedSessionAsync(
        string targetId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        CdpSession session = await _router
            .WaitForSessionAsync(targetId, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
        if (session.SessionId.Equals(sessionId, StringComparison.Ordinal))
        {
            return session;
        }

        // A concurrent attach for the same target can replace the unclaimed entry. Poll the
        // live list briefly so the caller still receives the session it just created.
        for (int i = 0; i < 50; i++)
        {
            foreach (CdpSession candidate in _router.Snapshot())
            {
                if (candidate.SessionId.Equals(sessionId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new CdpException($"CDP session '{sessionId}' did not attach in time.");
    }

    private void OnSessionDetached(CdpSession session)
    {
        BrowserContext? serviceWorkerContext;
        lock (_gate)
        {
            _serviceWorkerContexts.Remove(session.SessionId, out serviceWorkerContext);
        }

        serviceWorkerContext?.RemoveServiceWorker(session);

        CdpPage? childOwner;
        lock (_gate)
        {
            _sessionOwners.Remove(session.SessionId, out childOwner);
        }

        childOwner?.OnChildSessionDetached(session);

        CdpPage? page = null;
        lock (_gate)
        {
            for (int i = _pages.Count - 1; i >= 0; i--)
            {
                if (_pages[i].Session.SessionId.Equals(session.SessionId, StringComparison.Ordinal))
                {
                    page = _pages[i];
                    _pages.RemoveAt(i);
                    _sessionOwners.Remove(page.Session.SessionId);
                    break;
                }
            }
        }

        if (page is not null)
        {
            page.Context.RemovePage(page);
            page.MarkClosed();
            page.Context.RaisePageClosed(page);
            RaisePageClosed(page);
        }

        _sessionDetached?.Invoke(session);
    }

    private void RegisterOpenedPage(CdpSession session)
    {
        CdpPage? opener = FindPage(session.OpenerId);
        BrowserContext? context = opener?.Context ?? ResolveContext(session.BrowserContextId);
        if (context is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (CdpPage existing in _pages)
            {
                if (existing.TargetId.Equals(session.TargetId, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        CdpPage page = new(this, session, context);
        lock (_gate)
        {
            _pages.Add(page);
            _sessionOwners[session.SessionId] = page;
        }

        context.AddPage(page);
        _ = CompleteOpenedPageAsync(page, context, opener);
    }

    private async Task CompleteOpenedPageAsync(CdpPage page, BrowserContext context, CdpPage? opener)
    {
        try
        {
            await page.EnableAsync().ConfigureAwait(false);
            await context.ApplyToPageAsync(page, CancellationToken.None).ConfigureAwait(false);
            if (context.Options is { } options)
            {
                await page.ApplyContextOptionsAsync(options, CancellationToken.None).ConfigureAwait(false);
                if (options.RecordVideo is not null)
                {
                    await page.StartVideoAsync(options, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (context.Routes.HasRoutes)
            {
                await page.EnsureRouteInterceptionAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // The popup may close or navigate before emulation is applied; the page itself stays
            // usable and reports its own closed state.
        }
        finally
        {
            opener?.NotifyPopup(page);
            context.RaisePageOpened(page);
            RaisePageOpened(page);
        }
    }

    private BrowserContext? ResolveContext(string? browserContextId)
    {
        if (string.IsNullOrEmpty(browserContextId))
        {
            return DefaultContext;
        }

        lock (_gate)
        {
            foreach (BrowserContext context in _contexts)
            {
                if (context.BrowserContextId == browserContextId)
                {
                    return context;
                }
            }
        }

        return null;
    }

    internal CdpPage? FindPage(string? targetId)
    {
        if (string.IsNullOrEmpty(targetId))
        {
            return null;
        }

        lock (_gate)
        {
            foreach (CdpPage page in _pages)
            {
                if (page.TargetId.Equals(targetId, StringComparison.Ordinal))
                {
                    return page;
                }
            }
        }

        return null;
    }

    private void RaisePageOpened(CdpPage page)
    {
        Action<CdpPage>? handlers;
        lock (_pageEventGate)
        {
            handlers = _pageOpened;
        }

        handlers?.Invoke(page);
    }

    private void RaisePageClosed(CdpPage page)
    {
        Action<CdpPage>? handlers;
        lock (_pageEventGate)
        {
            handlers = _pageClosed;
        }

        handlers?.Invoke(page);
    }

}
