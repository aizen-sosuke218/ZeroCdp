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
    /// <summary>Reloads the current document.</summary>
    public async ValueTask<GotoResult> ReloadAsync(
        WaitUntil waitUntil = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        NavigationWaiter waiter = _state.BeginNavigation(EffectiveLifecycle(waitUntil));
        TimeSpan effectiveTimeout = timeout ?? DefaultNavigationTimeout;
        string url = await UrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (waitUntil == WaitUntil.NetworkIdle)
            {
                // Network observation has to be live before the first request leaves, otherwise a
                // fetch started from the load handler is invisible and the idle window closes early.
                await _network.EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            }

            await Session
                .SendAsync<Unit, ReloadParams>("Page.reload"u8, new ReloadParams(), cancellationToken)
                .ConfigureAwait(false);

            if (waitUntil != WaitUntil.Commit)
            {
                await waiter.Completion.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (waitUntil == WaitUntil.NetworkIdle)
            {
                await WaitForNetworkIdleAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }

            return new GotoResult(_mainFrameId, null, url);
        }
        catch (TimeoutException)
        {
            CdpNavigationException failure = new(
                url,
                $"no {waitUntil} signal after reload within {effectiveTimeout.TotalSeconds:0}s");
            waiter.Fail(failure);
            throw failure;
        }
        finally
        {
            _state.EndNavigation(waiter);
        }
    }

    /// <summary>Navigates to the previous history entry. Returns false when there is none.</summary>
    public async ValueTask<bool> GoBackAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await GoToHistoryEntryAsync(forward: false, timeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Navigates to the next history entry. Returns false when there is none.</summary>
    public async ValueTask<bool> GoForwardAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await GoToHistoryEntryAsync(forward: true, timeout, cancellationToken).ConfigureAwait(false);

    /// <summary>The browser's navigation history for this page.</summary>
    public ValueTask<NavigationHistoryResult> GetNavigationHistoryAsync(
        CancellationToken cancellationToken = default)
        => Session.SendAsync<NavigationHistoryResult>("Page.getNavigationHistory"u8, cancellationToken);

    /// <summary>
    /// Waits until the committed URL equals <paramref name="url"/>, or matches it as a glob when
    /// the value contains <c>*</c> or <c>?</c>.
    /// </summary>
    public async ValueTask<string> WaitForUrlAsync(
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        long deadline = Environment.TickCount64 + (long)effectiveTimeout.TotalMilliseconds;
        bool glob = url.Contains('*', StringComparison.Ordinal) || url.Contains('?', StringComparison.Ordinal);

        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            string current = await UrlAsync(cancellationToken).ConfigureAwait(false);
            if (glob ? UrlGlob.IsMatch(url, current) : current.Equals(url, StringComparison.Ordinal))
            {
                return current;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"URL did not become '{url}' within {effectiveTimeout.TotalSeconds:0.##}s (current: '{current}').");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until the main document reaches <paramref name="state"/> by polling
    /// <c>document.readyState</c> in the isolated world.
    /// </summary>
    /// <summary>Playwright spelling for <see cref="WaitForUrlAsync"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1708:Identifiers should differ by more than case",
        Justification = "Playwright uses WaitForURLAsync while the existing API uses WaitForUrlAsync.")]
    public ValueTask<string> WaitForURLAsync(
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForUrlAsync(url, timeout, cancellationToken);

    public async ValueTask WaitForLoadStateAsync(
        WaitUntil state = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (state == WaitUntil.Commit)
        {
            return;
        }

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;

        if (state == WaitUntil.NetworkIdle)
        {
            // Enable before the load wait so requests fired by the document's own scripts are seen.
            await _network.EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            await WaitForLoadStateAsync(WaitUntil.Load, effectiveTimeout, cancellationToken).ConfigureAwait(false);
            await WaitForNetworkIdleAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            return;
        }
        long deadline = Environment.TickCount64 + (long)effectiveTimeout.TotalMilliseconds;

        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

            try
            {
                EvaluationResult result = await EvaluateAsync("document.readyState", cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Threw
                    && result.Value.StringValue is string readyState
                    && (readyState == "complete" || (state == WaitUntil.DomContentLoaded && readyState == "interactive")))
                {
                    return;
                }
            }
            catch (CdpException)
            {
                // A committed navigation destroys the old execution context; the next poll creates
                // a fresh isolated world for the new document.
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Main document did not reach {state} within {effectiveTimeout.TotalSeconds:0.##}s.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static WaitUntil EffectiveLifecycle(WaitUntil waitUntil)
        => waitUntil == WaitUntil.NetworkIdle ? WaitUntil.Load : waitUntil;

    private async ValueTask WaitForNetworkIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => await _network
            .WaitForIdleAsync(TimeSpan.FromMilliseconds(500), timeout, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Replaces the main document's markup without a network navigation.</summary>
    public async ValueTask SetContentAsync(
        string html,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        string frameId = _mainFrameId
            ?? throw new CdpException("The main frame id is not known yet.");

        await Session
            .SendAsync<Unit, SetDocumentContentParams>(
                "Page.setDocumentContent"u8,
                new SetDocumentContentParams(frameId, html),
                cancellationToken)
            .ConfigureAwait(false);

        InvalidateWorlds();
    }

    /// <summary>Overrides the layout viewport for this page.</summary>

    private async ValueTask<bool> GoToHistoryEntryAsync(
        bool forward,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        NavigationHistoryResult history = await GetNavigationHistoryAsync(cancellationToken).ConfigureAwait(false);
        int index = history.CurrentIndex + (forward ? 1 : -1);
        if (index < 0 || index >= history.Entries.Count)
        {
            return false;
        }

        NavigationEntry entry = history.Entries[index];
        NavigationWaiter waiter = _state.BeginNavigation(WaitUntil.Commit);
        TimeSpan effectiveTimeout = timeout ?? DefaultNavigationTimeout;

        try
        {
            await Session
                .SendAsync<Unit, NavigateToHistoryEntryParams>(
                    "Page.navigateToHistoryEntry"u8,
                    new NavigateToHistoryEntryParams(entry.Id),
                    cancellationToken)
                .ConfigureAwait(false);

            await waiter.Completion.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            CdpNavigationException failure = new(
                entry.Url,
                $"history navigation did not commit within {effectiveTimeout.TotalSeconds:0}s");
            waiter.Fail(failure);
            throw failure;
        }
        finally
        {
            _state.EndNavigation(waiter);
        }
    }

    private void InvalidateWorlds()
    {
        _isolated.Invalidate();
        lock (_frameWorldsGate)
        {
            _frameWorlds.Clear();
        }
    }

    /// <summary>
    /// Navigates the main frame and waits for the requested lifecycle point.
    /// </summary>
    /// <exception cref="CdpNavigationException">The browser refused the navigation, or it timed out.</exception>
    public async ValueTask<GotoResult> GotoAsync(
        string url,
        WaitUntil waitUntil = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        url = ResolveNavigationUrl(url);

        // Checked before any command goes out, so a blocked navigation leaves the page untouched.
        _browser.NavigationPolicy.Validate(url);

        await EnableAsync(cancellationToken).ConfigureAwait(false);

        // Registered before the command goes out: a fast local page can fire `load` before the
        // navigate response comes back, and that signal must not be missed.
        NavigationWaiter waiter = _state.BeginNavigation(EffectiveLifecycle(waitUntil));
        TimeSpan effectiveTimeout = timeout ?? DefaultNavigationTimeout;

        try
        {
            if (waitUntil == WaitUntil.NetworkIdle)
            {
                // Start observing before Page.navigate, so every request the new document makes is
                // part of the idle window (including fetches started from the load handler).
                await _network.EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            }

            NavigateResult navigation = await Session
                .SendAsync<NavigateResult, NavigateParams>("Page.navigate"u8, new NavigateParams(url), cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(navigation.ErrorText))
            {
                CdpNavigationException failure = new(url, navigation.ErrorText!);
                waiter.Fail(failure);
                throw failure;
            }

            if (waitUntil != WaitUntil.Commit)
            {
                await waiter.Completion.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (waitUntil == WaitUntil.NetworkIdle)
            {
                await WaitForNetworkIdleAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }

            return new GotoResult(navigation.FrameId, navigation.LoaderId, url);
        }
        catch (TimeoutException)
        {
            CdpNavigationException timeoutFailure = new(url, $"no {waitUntil} signal within {effectiveTimeout.TotalSeconds:0}s");
            waiter.Fail(timeoutFailure);
            throw timeoutFailure;
        }
        finally
        {
            _state.EndNavigation(waiter);
        }
    }

    /// <summary>Current title and URL as the browser reports them for this target.</summary>
    public ValueTask<TargetInfoResult> GetInfoAsync(CancellationToken cancellationToken = default)
        => _browser.Connection.SendAsync<TargetInfoResult, GetTargetInfoParams>(
            "Target.getTargetInfo"u8,
            new GetTargetInfoParams(TargetId),
            cancellationToken);

    public async ValueTask<string> TitleAsync(CancellationToken cancellationToken = default)
    {
        // Playwright reads document.title, which reflects a title change immediately. The target
        // info copy can lag behind a click or script that mutates the DOM.
        try
        {
            EvaluationResult result = await EvaluateAsync("document.title", cancellationToken)
                .ConfigureAwait(false);
            if (!result.Threw)
            {
                return result.Value.StringValue ?? string.Empty;
            }
        }
        catch (CdpException)
        {
            // The document may not have an execution context yet; fall back to target metadata.
        }

        return (await GetInfoAsync(cancellationToken).ConfigureAwait(false)).Title;
    }

    public async ValueTask<string> UrlAsync(CancellationToken cancellationToken = default)
        => (await GetInfoAsync(cancellationToken).ConfigureAwait(false)).Url;

    /// <summary>
    /// Evaluates JavaScript in this page's isolated world and returns its value.
    /// </summary>
    /// <remarks>
    /// The Runtime domain is not enabled. The first call creates the isolated world through
    /// <c>Page.createIsolatedWorld</c>; later calls reuse its context until a navigation commits.
    /// A JavaScript exception is reported through <see cref="EvaluationResult.Threw"/> instead of
    /// being thrown as a protocol error.
    /// </remarks>

    private void OnMainFrameNavigated()
    {
        ClearWebSocketRoutes();
        InvalidateWorlds();
    }

    private readonly Lock _navigationWaitGate = new();

    private readonly List<NavigationWaitRequest> _navigationWaitRequests = new();

    /// <summary>
    /// Waits for the main frame to commit a navigation. Call this before the action that navigates,
    /// or use <see cref="RunAndWaitForNavigationAsync"/>.
    /// </summary>
    /// <returns>The committed URL.</returns>
    public async ValueTask<string> WaitForNavigationAsync(
        string? urlPattern = null,
        WaitUntil waitUntil = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan effectiveTimeout = timeout ?? DefaultNavigationTimeout;
        NavigationWaitRequest request = new(urlPattern);
        lock (_navigationWaitGate)
        {
            _navigationWaitRequests.Add(request);
        }

        try
        {
            string url = await request.Completion.Task
                .WaitAsync(effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (waitUntil != WaitUntil.Commit)
            {
                await WaitForLoadStateAsync(waitUntil, effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }

            return url;
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No navigation{(urlPattern is null ? string.Empty : $" matching '{urlPattern}'")} " +
                $"committed within {effectiveTimeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            lock (_navigationWaitGate)
            {
                _navigationWaitRequests.Remove(request);
            }
        }
    }

    /// <summary>Playwright-style navigation overload using an options object.</summary>
    public ValueTask<string> WaitForNavigationAsync(
        PageWaitForNavigationOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WaitForNavigationAsync(
            options.Url,
            options.WaitUntil?.ToWaitUntil() ?? WaitUntil.Load,
            options.Timeout,
            cancellationToken);
    }

    /// <summary>Runs an action and returns the URL of the navigation it triggers.</summary>
    public async ValueTask<string> RunAndWaitForNavigationAsync(
        Func<ValueTask> action,
        string? urlPattern = null,
        WaitUntil waitUntil = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValueTask<string> waiting = WaitForNavigationAsync(urlPattern, waitUntil, timeout, cancellationToken);
        await action().ConfigureAwait(false);
        return await waiting.ConfigureAwait(false);
    }

    private void OnMainFrameCommitted(string url)
    {
        NavigationWaitRequest[] waiters;
        lock (_navigationWaitGate)
        {
            if (_navigationWaitRequests.Count == 0)
            {
                return;
            }

            waiters = _navigationWaitRequests.ToArray();
        }

        foreach (NavigationWaitRequest waiter in waiters)
        {
            if (waiter.UrlPattern is null || UrlGlob.IsMatch(waiter.UrlPattern, url))
            {
                waiter.Completion.TrySetResult(url);
            }
        }
    }

    /// <summary>Playwright-style navigation overload using an options object.</summary>
    public ValueTask<GotoResult> GotoAsync(
        string url,
        PageGotoOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GotoAsync(
            url,
            options.WaitUntil?.ToWaitUntil() ?? WaitUntil.Load,
            options.Timeout,
            cancellationToken);
    }

    /// <summary>Playwright-style reload overload using an options object.</summary>
    public ValueTask<GotoResult> ReloadAsync(
        PageReloadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ReloadAsync(
            options.WaitUntil?.ToWaitUntil() ?? WaitUntil.Load,
            options.Timeout,
            cancellationToken);
    }

    /// <summary>Playwright-style load-state overload using an options object.</summary>
    public ValueTask WaitForLoadStateAsync(
        LoadState state,
        PageWaitForLoadStateOptions? options = null,
        CancellationToken cancellationToken = default)
        => WaitForLoadStateAsync(state.ToWaitUntil(), options?.Timeout, cancellationToken);
}

internal sealed class NavigationWaitRequest
{
    public NavigationWaitRequest(string? urlPattern) => UrlPattern = urlPattern;

    public string? UrlPattern { get; }

    public TaskCompletionSource<string> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
