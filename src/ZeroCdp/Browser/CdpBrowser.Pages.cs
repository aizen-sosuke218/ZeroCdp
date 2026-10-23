using System.Text;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Security;

namespace ZeroCdp.Browser;

public sealed partial class CdpBrowser
{
    /// <summary>
    /// Creates an isolated incognito context. Pages in this context share cookies, storage and
    /// cache with each other, but not with the default context.
    /// </summary>
    public async ValueTask<BrowserContext> NewContextAsync(
        BrowserContextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        options ??= new BrowserContextOptions();
        options.Validate();

        BrowserContextCreated created = await _connection
            .SendAsync<BrowserContextCreated, CreateBrowserContextParams>(
                "Target.createBrowserContext"u8,
                new CreateBrowserContextParams(
                    disposeOnDetach: true,
                    options.ProxyServer,
                    options.ProxyBypassList),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(created.BrowserContextId))
        {
            throw new CdpProtocolException("Target.createBrowserContext returned no context id.");
        }

        BrowserContext context = new(this, created.BrowserContextId, options);
        lock (_gate)
        {
            _contexts.Add(context);
        }

        try
        {
            if (options.IgnoreHttpsErrors)
            {
                await _connection
                    .SendAsync<Unit, SetIgnoreCertificateErrorsParams>(
                        "Security.setIgnoreCertificateErrors"u8,
                        new SetIgnoreCertificateErrorsParams(true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.Permissions.Count > 0)
            {
                await context.GrantPermissionsAsync(options.Permissions, origin: null, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.StorageState is { } storageState)
            {
                await context.SetStorageStateAsync(storageState, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                _contexts.Remove(context);
            }

            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return context;
    }

    internal async ValueTask SetOriginStorageAsync(
        BrowserContext context,
        IReadOnlyList<OriginStorageState> origins,
        CancellationToken cancellationToken)
    {
        bool hasStorage = false;
        foreach (OriginStorageState origin in origins)
        {
            if (origin.LocalStorage.Count > 0 || origin.IndexedDb.Count > 0)
            {
                hasStorage = true;
                break;
            }
        }

        if (!hasStorage)
        {
            return;
        }

        // DOMStorage is target-scoped and Chromium only accepts a storage id for a frame that has
        // committed the matching origin. Borrow an about:blank target, navigate each origin
        // through a locally fulfilled Fetch request, write its entries, then close the target.
        // The target is never registered as a CdpPage, so it cannot surface in page lists or
        // page events.
        TargetCreated created = await _connection
            .SendAsync<TargetCreated, CreateTargetParams>(
                "Target.createTarget"u8,
                new CreateTargetParams("about:blank", context.BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(created.TargetId))
        {
            throw new CdpProtocolException("Target.createTarget returned no target id.");
        }

        try
        {
            CdpSession session = await _router
                .WaitForSessionAsync(created.TargetId, AttachTimeout, cancellationToken)
                .ConfigureAwait(false);

            await session.SendAsync<Unit>("Page.enable"u8, cancellationToken).ConfigureAwait(false);
            await session
                .SendAsync<Unit, EnableFetchParams>(
                    "Fetch.enable"u8,
                    new EnableFetchParams(),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (OriginStorageState origin in origins)
            {
                if (string.IsNullOrEmpty(origin.Origin)
                    || (origin.LocalStorage.Count == 0 && origin.IndexedDb.Count == 0))
                {
                    continue;
                }

                StorageBootstrapSink sink = new();
                session.EventSink = sink;
                string navigationUrl = origin.Origin.EndsWith('/')
                    ? origin.Origin
                    : origin.Origin + "/";

                // Page.navigate waits for the document request to be answered, so it cannot be
                // awaited before fulfilling the paused Fetch request.
                Task<NavigateResult> navigation = session
                    .SendAsync<NavigateResult, NavigateParams>(
                        "Page.navigate"u8,
                        new NavigateParams(navigationUrl),
                        cancellationToken)
                    .AsTask();

                string requestId;
                try
                {
                    requestId = await sink.RequestId
                        .WaitAsync(AttachTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new CdpTimeoutException(
                        $"Timed out preparing local storage for origin '{origin.Origin}'.");
                }

                await session
                    .SendAsync<Unit, FulfillRequestParams>(
                        "Fetch.fulfillRequest"u8,
                        new FulfillRequestParams(
                            requestId,
                            200,
                            [new CdpHeader("Content-Type", "text/html; charset=utf-8")],
                            StorageBootstrapBodyBase64),
                        cancellationToken)
                    .ConfigureAwait(false);

                NavigateResult navigated = await navigation.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(navigated.ErrorText))
                {
                    throw new CdpNavigationException(navigationUrl, navigated.ErrorText);
                }

                try
                {
                    await sink.NavigationUrl
                        .WaitAsync(AttachTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new CdpTimeoutException(
                        $"Timed out committing local storage origin '{origin.Origin}'.");
                }

                foreach (StorageEntry entry in origin.LocalStorage)
                {
                    await session
                        .SendAsync<Unit, SetDOMStorageItemParams>(
                            "DOMStorage.setDOMStorageItem"u8,
                            new SetDOMStorageItemParams(origin.Origin, entry.Name, entry.Value),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (origin.IndexedDb.Count > 0)
                {
                    string script = IndexedDbJson.BuildRestoreScript(origin.IndexedDb);
                    EvaluationResult restore = await session
                        .SendAsync<EvaluationResult, EvaluateParams>(
                            "Runtime.evaluate"u8,
                            EvaluateParams.MainWorld(script),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (restore.Threw)
                    {
                        throw new CdpException(
                            restore.ExceptionSummary
                            ?? $"Unable to restore IndexedDB for origin '{origin.Origin}'.");
                    }
                }
            }
        }
        finally
        {
            try
            {
                await _connection
                    .SendAsync<Unit, CloseTargetParams>(
                        "Target.closeTarget"u8,
                        new CloseTargetParams(created.TargetId),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // The browser may already be gone; the storage write is what matters.
            }
        }
    }

    internal async ValueTask<CdpPage> CreatePageAsync(
        BrowserContext context,
        string url,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ObjectDisposedException.ThrowIf(context.IsDisposed, context);
        ArgumentException.ThrowIfNullOrEmpty(url);

        if (!url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            NavigationPolicy.Validate(url);
        }

        BrowserContextOptions? options = context.Options;
        bool hasContextRoutes = context.Routes.HasRoutes;
        bool deferNavigation = !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            && (options is not null || hasContextRoutes || context.HasWebSocketRoutes);

        string targetUrl = deferNavigation ? "about:blank" : url;
        TargetCreated created = await _connection
            .SendAsync<TargetCreated, CreateTargetParams>(
                "Target.createTarget"u8,
                new CreateTargetParams(targetUrl, context.BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(created.TargetId))
        {
            throw new CdpProtocolException("Target.createTarget returned no target id.");
        }

        CdpSession session = await _router
            .WaitForSessionAsync(created.TargetId, AttachTimeout, cancellationToken)
            .ConfigureAwait(false);

        CdpPage page = new(this, session, context);
        lock (_gate)
        {
            _pages.Add(page);
            _sessionOwners[session.SessionId] = page;
        }

        context.AddPage(page);

        try
        {
            await page.EnableAsync(cancellationToken).ConfigureAwait(false);
            await context.ApplyToPageAsync(page, cancellationToken).ConfigureAwait(false);

            if (options is not null)
            {
                await page.ApplyContextOptionsAsync(options, cancellationToken).ConfigureAwait(false);
                if (options.RecordVideo is not null)
                {
                    await page.StartVideoAsync(options, cancellationToken).ConfigureAwait(false);
                }
            }

            if (context.Routes.HasRoutes)
            {
                await page.EnsureRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
            }

            if (deferNavigation)
            {
                await page.GotoAsync(url, WaitUntil.Load, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return page;
    }

    /// <summary>Closes the target a page is bound to.</summary>
    public async ValueTask ClosePageAsync(CdpPage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        lock (_gate)
        {
            _pages.Remove(page);
        }

        page.Context.RemovePage(page);

        await _connection
            .SendAsync<Unit, CloseTargetParams>("Target.closeTarget"u8, new CloseTargetParams(page.TargetId), cancellationToken)
            .ConfigureAwait(false);

        page.Context.RaisePageClosed(page);
        RaisePageClosed(page);
    }

    internal async ValueTask CloseContextAsync(BrowserContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.DisposeApiRequestAsync().ConfigureAwait(false);

        if (context.IsDefault)
        {
            CdpPage[] pages = context.SnapshotPages();
            foreach (CdpPage page in pages)
            {
                try
                {
                    await page.CloseAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CdpException)
                {
                    // The browser may already be gone.
                }
            }

            return;
        }

        if (context.MarkDisposed())
        {
            await context.WriteHarAsync(cancellationToken).ConfigureAwait(false);
            foreach (CdpPage page in context.SnapshotPages())
            {
                await page.StopVideoAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _contexts.Remove(context);
                foreach (CdpPage page in context.SnapshotPages())
                {
                    _pages.Remove(page);
                    page.MarkClosed();
                }
            }

            await _connection
                .SendAsync<Unit, DisposeBrowserContextParams>(
                    "Target.disposeBrowserContext"u8,
                    new DisposeBrowserContextParams(context.BrowserContextId!),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        context.ClearPages();
    }

}
