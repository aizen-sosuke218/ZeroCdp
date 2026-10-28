using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    /// <summary>Returns every cookie in this context's jar.</summary>
    public async ValueTask<CookiesResult> GetCookiesAsync(CancellationToken cancellationToken = default)
        => await _browser.Connection
            .SendAsync<CookiesResult, StorageGetCookiesParams>(
                "Storage.getCookies"u8,
                new StorageGetCookiesParams(BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Playwright-style alias for <see cref="GetCookiesAsync"/>.</summary>
    public async ValueTask<IReadOnlyList<CdpCookie>> CookiesAsync(
        CancellationToken cancellationToken = default)
    {
        CookiesResult result = await GetCookiesAsync(cancellationToken).ConfigureAwait(false);
        return result.Cookies;
    }

    /// <summary>Stores cookies in this context's jar.</summary>
    public async ValueTask SetCookiesAsync(
        IReadOnlyList<CdpCookie> cookies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        await _browser.Connection
            .SendAsync<Unit, StorageSetCookiesParams>(
                "Storage.setCookies"u8,
                new StorageSetCookiesParams(cookies, BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Playwright-style alias for <see cref="SetCookiesAsync"/>.</summary>
    public ValueTask AddCookiesAsync(
        IReadOnlyList<CdpCookie> cookies,
        CancellationToken cancellationToken = default)
        => SetCookiesAsync(cookies, cancellationToken);

    /// <summary>Clears every cookie in this context.</summary>
    public async ValueTask ClearCookiesAsync(CancellationToken cancellationToken = default)
        => await _browser.Connection
            .SendAsync<Unit, StorageClearCookiesParams>(
                "Storage.clearCookies"u8,
                new StorageClearCookiesParams(BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Clears cookies matching the supplied filters. Unset filters match every cookie, so this
    /// also acts as an unfiltered clear when <paramref name="options"/> is null.
    /// </summary>
    /// <remarks>
    /// Chromium exposes no filtered clear, so matching cookies are removed by clearing the jar and
    /// restoring the rest, which is how Playwright implements the same operation.
    /// </remarks>
    public async ValueTask ClearCookiesAsync(
        BrowserContextClearCookiesOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            await ClearCookiesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        CookiesResult existing = await GetCookiesAsync(cancellationToken).ConfigureAwait(false);
        List<CdpCookie> keep = new();
        foreach (CdpCookie cookie in existing.Cookies)
        {
            bool matches = (options.Name is null
                    || string.Equals(cookie.Name, options.Name, StringComparison.Ordinal))
                && (options.Domain is null
                    || string.Equals(
                        cookie.Domain.TrimStart('.'),
                        options.Domain.TrimStart('.'),
                        StringComparison.OrdinalIgnoreCase))
                && (options.Path is null
                    || string.Equals(cookie.Path, options.Path, StringComparison.Ordinal));
            if (!matches)
            {
                keep.Add(cookie);
            }
        }

        await ClearCookiesAsync(cancellationToken).ConfigureAwait(false);
        if (keep.Count > 0)
        {
            await SetCookiesAsync(keep, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Captures cookies plus <c>localStorage</c> for every HTTP(S) page currently open in this
    /// context. Origins with no open page cannot be enumerated, which matches Playwright's
    /// storage-state collection model.
    /// </summary>
    public ValueTask<StorageState> StorageStateAsync(CancellationToken cancellationToken = default)
        => StorageStateAsyncCore(indexedDb: false, cancellationToken);

    /// <summary>
    /// Captures cookies, <c>localStorage</c>, and optionally IndexedDB for every HTTP(S) page
    /// currently open in this context.
    /// </summary>
    public ValueTask<StorageState> StorageStateAsync(
        StorageStateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return StorageStateAsyncCore(options.IndexedDb, cancellationToken);
    }

    private async ValueTask<StorageState> StorageStateAsyncCore(
        bool indexedDb,
        CancellationToken cancellationToken)
    {
        CookiesResult cookies = await GetCookiesAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, OriginBuilder> byOrigin = new(StringComparer.Ordinal);

        foreach (CdpPage page in Pages)
        {
            if (page.IsClosed)
            {
                continue;
            }

            string origin;
            try
            {
                string url = await page.UrlAsync(cancellationToken).ConfigureAwait(false);
                if (!TryGetHttpOrigin(url, out origin))
                {
                    continue;
                }

                IReadOnlyList<StorageEntry> entries;
                IReadOnlyList<IndexedDbDatabase> databases;
                if (indexedDb)
                {
                    EvaluationResult result = await page
                        .EvaluateAsync(IndexedDbScript.CollectTemplate, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Threw
                        || !StorageStateJson.TryParseCollectedStorage(
                            result.Value.StringValue,
                            out entries,
                            out databases))
                    {
                        continue;
                    }
                }
                else
                {
                    EvaluationResult result = await page
                        .EvaluateAsync("JSON.stringify(Object.entries(localStorage))", cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Threw
                        || !StorageStateJson.TryParseLocalStorageEntries(
                            result.Value.StringValue,
                            out entries))
                    {
                        continue;
                    }

                    databases = Array.Empty<IndexedDbDatabase>();
                }

                if (!byOrigin.TryGetValue(origin, out OriginBuilder? builder))
                {
                    builder = new OriginBuilder(origin);
                    byOrigin.Add(origin, builder);
                }

                builder.AddLocalStorage(entries);
                builder.AddIndexedDb(databases);
            }
            catch (CdpException)
            {
                // A page can navigate between the URL read and the storage read. Skipping it
                // is preferable to failing the whole snapshot for one racing page.
            }
        }

        List<OriginStorageState> origins = new(byOrigin.Count);
        foreach (OriginBuilder builder in byOrigin.Values)
        {
            origins.Add(builder.Build());
        }

        origins.Sort(static (left, right) => string.CompareOrdinal(left.Origin, right.Origin));
        return new StorageState { Cookies = cookies.Cookies, Origins = origins };
    }

    /// <summary>
    /// Adds cookies and <c>localStorage</c> entries to this context. Local storage is written
    /// through the browser's storage backend, so it exists before the first page navigates to an
    /// origin.
    /// </summary>
    public async ValueTask SetStorageStateAsync(
        StorageState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (state.Cookies.Count > 0)
        {
            await SetCookiesAsync(state.Cookies, cancellationToken).ConfigureAwait(false);
        }

        await _browser.SetOriginStorageAsync(this, state.Origins, cancellationToken).ConfigureAwait(false);
    }

    private sealed class OriginBuilder
    {
        private readonly Dictionary<string, string> _localStorage = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IndexedDbDatabase> _indexedDb = new(StringComparer.Ordinal);

        public OriginBuilder(string origin) => Origin = origin;

        public string Origin { get; }

        public void AddLocalStorage(IReadOnlyList<StorageEntry> entries)
        {
            foreach (StorageEntry entry in entries)
            {
                _localStorage[entry.Name] = entry.Value;
            }
        }

        public void AddIndexedDb(IReadOnlyList<IndexedDbDatabase> databases)
        {
            foreach (IndexedDbDatabase database in databases)
            {
                if (database.Name.Length > 0)
                {
                    _indexedDb[database.Name] = database;
                }
            }
        }

        public OriginStorageState Build()
        {
            List<StorageEntry> entries = new(_localStorage.Count);
            foreach ((string name, string value) in _localStorage)
            {
                entries.Add(new StorageEntry { Name = name, Value = value });
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

            List<IndexedDbDatabase> databases = new(_indexedDb.Count);
            databases.AddRange(_indexedDb.Values);
            databases.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

            return new OriginStorageState
            {
                Origin = Origin,
                LocalStorage = entries,
                IndexedDb = databases,
            };
        }
    }

    private static bool TryGetHttpOrigin(string url, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return origin.Length > 0;
    }

    /// <summary>
    /// Grants browser permissions (for example <c>geolocation</c>, <c>notifications</c>,
    /// <c>camera</c>) to pages in this context.
    /// </summary>
    public async ValueTask GrantPermissionsAsync(
        IReadOnlyList<string> permissions,
        string? origin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        await _browser.Connection
            .SendAsync<Unit, GrantPermissionsParams>(
                "Browser.grantPermissions"u8,
                new GrantPermissionsParams(permissions, origin, BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Grants one permission to the whole context.</summary>
    public ValueTask GrantPermissionsAsync(
        string permission,
        string? origin = null,
        CancellationToken cancellationToken = default)
        => GrantPermissionsAsync([permission], origin, cancellationToken);

    /// <summary>Resets all permissions granted in this context.</summary>
    public async ValueTask ClearPermissionsAsync(CancellationToken cancellationToken = default)
        => await _browser.Connection
            .SendAsync<Unit, ResetPermissionsParams>(
                "Browser.resetPermissions"u8,
                new ResetPermissionsParams(BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Extra HTTP headers for every page currently open in this context.
    /// </summary>
    /// <remarks>
    /// <c>Network.setExtraHTTPHeaders</c> is a page-target command and Chromium ignores a context id
    /// on it, so the value is pushed to each page session. Pages created later get the value from
    /// <see cref="BrowserContextOptions.ExtraHttpHeaders"/>; passing an empty list removes the
    /// headers from the current pages.
    /// </remarks>
    public async ValueTask SetExtraHttpHeadersAsync(
        IReadOnlyList<CdpHeader> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (CdpPage page in SnapshotPages())
        {
            // The Network domain has to be on for Chromium to apply the override.
            await page.EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            await page.SetExtraHTTPHeadersAsync(headers, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Puts every page in this context offline, or back online.</summary>
    /// <remarks>
    /// <c>Network.emulateNetworkConditions</c> is a page-target command, so the setting is pushed to
    /// each open page (and the Network domain is enabled on those pages).
    /// </remarks>
    public async ValueTask SetOfflineAsync(bool offline, CancellationToken cancellationToken = default)
    {
        foreach (CdpPage page in SnapshotPages())
        {
            await page.EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            await page.SetOfflineAsync(offline, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Secures every page with HTTP credentials, or clears them when null.</summary>
    public async ValueTask SetHttpCredentialsAsync(
        HttpCredentials? credentials,
        CancellationToken cancellationToken = default)
    {
        foreach (CdpPage page in SnapshotPages())
        {
            await page.SetHttpCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Emulates media type and features for every page currently in this context.</summary>
    public async ValueTask EmulateMediaAsync(
        string? media = null,
        PreferredColorScheme? colorScheme = null,
        string? reducedMotion = null,
        string? forcedColors = null,
        CancellationToken cancellationToken = default)
    {
        foreach (CdpPage page in SnapshotPages())
        {
            await page
                .EmulateMediaAsync(media, colorScheme, reducedMotion, forcedColors, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Overrides geolocation for every page currently in this context, or clears the override when
    /// <paramref name="geolocation"/> is null.
    /// </summary>
    /// <remarks>
    /// Pages created afterwards get the context option value from
    /// <see cref="BrowserContextOptions.Geolocation"/>; Chromium only accepts the override per page.
    /// </remarks>
    public async ValueTask SetGeolocationAsync(
        Geolocation? geolocation,
        CancellationToken cancellationToken = default)
    {
        foreach (CdpPage page in SnapshotPages())
        {
            if (geolocation is { } location)
            {
                await page.SetGeolocationAsync(location, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await page.ClearGeolocationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Clears the geolocation override for every page currently in this context.</summary>
    public ValueTask ClearGeolocationAsync(CancellationToken cancellationToken = default)
        => SetGeolocationAsync(geolocation: null, cancellationToken);

}
