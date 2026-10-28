using System.Text;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

internal sealed partial class NetworkState
{
    public async ValueTask RouteAsync(
        string urlPattern,
        Func<Route, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(urlPattern);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_routeGate)
        {
            _routes.Add(new RouteRegistration(urlPattern, handler));
        }

        try
        {
            await EnableFetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await UnrouteAsync(urlPattern, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask UnrouteAsync(string? urlPattern, CancellationToken cancellationToken)
    {
        bool any;
        lock (_routeGate)
        {
            if (string.IsNullOrEmpty(urlPattern))
            {
                _routes.Clear();
            }
            else
            {
                _routes.RemoveAll(route => route.Pattern.Equals(urlPattern, StringComparison.Ordinal));
            }

            any = _routes.Count > 0
                || _contextRoutes?.HasRoutes == true
                || _httpCredentials is not null;
        }

        if (!any)
        {
            await DisableFetchAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RefreshRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Enables Fetch interception because a context-level route was registered.</summary>
    public ValueTask EnsureRouteInterceptionAsync(CancellationToken cancellationToken)
        => EnableFetchAsync(cancellationToken);

    /// <summary>
    /// Installs (or removes) the transform applied to document HTML before Chromium parses it.
    /// This is how <c>AddInitScriptAsync</c> runs before page scripts without using
    /// <c>Page.addScriptToEvaluateOnNewDocument</c>.
    /// </summary>
    public async ValueTask SetDocumentTransformerAsync(
        Func<string, string>? transformer,
        CancellationToken cancellationToken)
    {
        lock (_routeGate)
        {
            if (ReferenceEquals(_documentTransformer, transformer))
            {
                return;
            }

            _documentTransformer = transformer;
        }

        // The Fetch pattern set depends on whether a response stage is needed, so the domain is
        // re-enabled instead of mutated in place.
        await DisableFetchAsync(cancellationToken).ConfigureAwait(false);
        await RefreshRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-enables or disables Fetch after context routes changed.</summary>
    public async ValueTask RefreshRouteInterceptionAsync(CancellationToken cancellationToken)
    {
        bool any;
        lock (_routeGate)
        {
            any = _routes.Count > 0
                || _contextRoutes?.HasRoutes == true
                || _documentTransformer is not null
                || _httpCredentials is not null;
        }

        if (any)
        {
            await EnableFetchAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DisableFetchAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleRequestPaused(ReadOnlySpan<byte> paramsUtf8)
    {
        // A response-stage pause carries a status code; request-stage pauses do not.
        if (TryReadFetchResponse(paramsUtf8, out FetchResponseEvent responseEvent))
        {
            HandleDocumentResponse(responseEvent);
            return;
        }

        if (!NetworkInfoParser.TryParseRequest(paramsUtf8, fetchShape: true, out PageRequest request))
        {
            return;
        }

        Touch();
        RememberRequest(request);

        // With the Network domain off, Fetch is the only source of request events. When Network is
        // enabled, requestWillBeSent has already reported the same request, so avoid duplicate events.
        if (Volatile.Read(ref _networkEnabled) == 0)
        {
            RequestStarted?.Invoke(request);
        }

        List<Func<Route, ValueTask>>? chain = null;
        lock (_routeGate)
        {
            for (int i = _routes.Count - 1; i >= 0; i--)
            {
                RouteRegistration registration = _routes[i];
                if (UrlGlob.IsMatch(registration.Pattern, request.Url))
                {
                    (chain ??= new List<Func<Route, ValueTask>>()).Add(registration.Handler);
                }
            }
        }

        // Page-level routes take priority over context routes. Within each scope, the most
        // recently registered matching route wins; Route.FallbackAsync walks the rest.
        if (_contextRoutes?.FindAll(request.Url) is { Length: > 0 } contextHandlers)
        {
            (chain ??= new List<Func<Route, ValueTask>>()).AddRange(contextHandlers);
        }

        if (chain is null || chain.Count == 0)
        {
            _ = ContinueQuietlyAsync(request.RequestId);
            return;
        }

        Func<Route, ValueTask>[] handlers = chain.ToArray();
        Route route = new(this, request, handlers);
        _ = Task.Run(() => InvokeHandlerAsync(handlers[0], route));
    }

    private static async Task InvokeHandlerAsync(Func<Route, ValueTask> handler, Route route)
    {
        try
        {
            await handler(route).ConfigureAwait(false);
            if (!route.Handled)
            {
                await route.ContinueAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!route.Handled)
            {
                try
                {
                    await route.AbortAsync("Failed").ConfigureAwait(false);
                }
                catch (Exception abortFailure) when (abortFailure is CdpException or ObjectDisposedException)
                {
                    // The page or browser is gone; nothing is left to resume.
                }
            }
        }
    }

    private async Task ContinueQuietlyAsync(string requestId)
    {
        try
        {
            await ContinueAsync(new ContinueRequestParams(requestId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // A target that closed while its request was paused needs no resume.
        }
    }

    internal async ValueTask ContinueAsync(ContinueRequestParams parameters, CancellationToken cancellationToken)
        => await _session.SendAsync<Unit, ContinueRequestParams>(
            "Fetch.continueRequest"u8,
            parameters,
            cancellationToken).ConfigureAwait(false);

    internal async ValueTask FulfillAsync(FulfillRequestParams parameters, CancellationToken cancellationToken)
        => await _session.SendAsync<Unit, FulfillRequestParams>(
            "Fetch.fulfillRequest"u8,
            parameters,
            cancellationToken).ConfigureAwait(false);

    internal async ValueTask FailAsync(FailRequestParams parameters, CancellationToken cancellationToken)
        => await _session.SendAsync<Unit, FailRequestParams>(
            "Fetch.failRequest"u8,
            parameters,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Supplies HTTP credentials for authentication challenges. Fetch stays enabled while
    /// credentials are configured even when no route exists, so the challenge is answered without
    /// a browser prompt.
    /// </summary>
    public async ValueTask SetHttpCredentialsAsync(
        HttpCredentials? credentials,
        CancellationToken cancellationToken)
    {
        bool hadCredentials;
        lock (_routeGate)
        {
            hadCredentials = _httpCredentials is not null;
            _httpCredentials = credentials;
        }

        if (credentials is not null || hadCredentials)
        {
            await RefreshRouteInterceptionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EnableFetchAsync(CancellationToken cancellationToken)
    {
        bool responseStage = Volatile.Read(ref _documentTransformer) is not null;
        bool handleAuth;
        lock (_routeGate)
        {
            handleAuth = _httpCredentials is not null;
        }

        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fetchEnabled && _fetchResponseStage == responseStage && _fetchAuth == handleAuth)
            {
                return;
            }

            if (_fetchEnabled)
            {
                try
                {
                    await _session.SendAsync<Unit>("Fetch.disable"u8, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _fetchEnabled = false;
                }
            }

            await _session
                .SendAsync<Unit, EnableFetchParams>(
                    "Fetch.enable"u8,
                    new EnableFetchParams(responseStage, handleAuth),
                    cancellationToken)
                .ConfigureAwait(false);
            _fetchEnabled = true;
            _fetchResponseStage = responseStage;
            _fetchAuth = handleAuth;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private void HandleAuthRequired(ReadOnlySpan<byte> paramsUtf8)
    {
        if (!TryReadAuthRequired(paramsUtf8, out string requestId, out string url))
        {
            return;
        }

        HttpCredentials? credentials;
        lock (_routeGate)
        {
            credentials = _httpCredentials;
        }

        _ = Task.Run(async () =>
        {
            ContinueWithAuthParams parameters;
            if (credentials is null)
            {
                parameters = ContinueWithAuthParams.Default(requestId);
            }
            else if (!string.IsNullOrEmpty(credentials.Origin)
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? requestUri)
                && Uri.TryCreate(credentials.Origin, UriKind.Absolute, out Uri? originUri)
                && !string.Equals(requestUri.GetLeftPart(UriPartial.Authority), originUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                parameters = ContinueWithAuthParams.Default(requestId);
            }
            else
            {
                parameters = ContinueWithAuthParams.ProvideCredentials(
                    requestId,
                    credentials.Username,
                    credentials.Password);
            }

            try
            {
                await _session
                    .SendAsync<Unit, ContinueWithAuthParams>(
                        "Fetch.continueWithAuth"u8,
                        parameters,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
            {
                // The target went away while the challenge was being answered.
            }
        });
    }

    private static bool TryReadAuthRequired(
        ReadOnlySpan<byte> paramsUtf8,
        out string requestId,
        out string url)
    {
        requestId = string.Empty;
        url = string.Empty;
        ZeroCdp.Json.JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (name.SequenceEqual("request"u8))
            {
                if (cursor.TryEnterObject())
                {
                    while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
                    {
                        if (property.SequenceEqual("url"u8))
                        {
                            url = CdpHeader.ReadString(ref cursor);
                        }
                        else if (!cursor.SkipValue())
                        {
                            return false;
                        }
                    }
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return requestId.Length > 0;
    }

    private void HandleDocumentResponse(FetchResponseEvent response)
    {
        Func<string, string>? transformer = Volatile.Read(ref _documentTransformer);
        if (transformer is null
            || response.StatusCode is < 200 or 204 or 304
            || !IsHtml(response.Headers))
        {
            _ = ContinueQuietlyAsync(response.RequestId);
            return;
        }

        _ = Task.Run(() => RewriteDocumentAsync(response, transformer));
    }

    private async Task RewriteDocumentAsync(FetchResponseEvent response, Func<string, string> transformer)
    {
        try
        {
            ResponseBodyResult body = await _session
                .SendAsync<ResponseBodyResult, GetResponseBodyParams>(
                    "Fetch.getResponseBody"u8,
                    new GetResponseBodyParams(response.RequestId),
                    CancellationToken.None)
                .ConfigureAwait(false);

            byte[] bytes = body.Base64Encoded
                ? Convert.FromBase64String(body.Body)
                : Encoding.UTF8.GetBytes(body.Body);

            string html = Encoding.UTF8.GetString(bytes);
            string rewritten = transformer(html);
            if (rewritten.Equals(html, StringComparison.Ordinal))
            {
                await ContinueQuietlyAsync(response.RequestId).ConfigureAwait(false);
                return;
            }

            List<CdpHeader> headers = new(response.Headers.Count);
            foreach (CdpHeader header in response.Headers)
            {
                if (header.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                headers.Add(header);
            }

            await FulfillAsync(
                    new FulfillRequestParams(
                        response.RequestId,
                        response.StatusCode,
                        headers,
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(rewritten))),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or FormatException)
        {
            await ContinueQuietlyAsync(response.RequestId).ConfigureAwait(false);
        }
    }

    private static bool IsHtml(IReadOnlyList<CdpHeader> headers)
    {
        foreach (CdpHeader header in headers)
        {
            if (header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value.Contains("text/html", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool TryReadFetchResponse(ReadOnlySpan<byte> paramsUtf8, out FetchResponseEvent value)
    {
        value = default;
        string requestId = string.Empty;
        string resourceType = string.Empty;
        int statusCode = 0;
        bool hasStatus = false;
        List<CdpHeader> headers = new();

        ZeroCdp.Json.JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (name.SequenceEqual("responseStatusCode"u8))
            {
                hasStatus = cursor.TryGetInt32(out statusCode);
            }
            else if (name.SequenceEqual("resourceType"u8))
            {
                resourceType = CdpHeader.ReadString(ref cursor);
            }
            else if (name.SequenceEqual("responseHeaders"u8))
            {
                ReadFetchHeaders(ref cursor, headers);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (!hasStatus || requestId.Length == 0)
        {
            return false;
        }

        value = new FetchResponseEvent(requestId, statusCode, resourceType, headers.ToArray());
        return true;
    }

    private static void ReadFetchHeaders(ref ZeroCdp.Json.JsonCursor cursor, List<CdpHeader> headers)
    {
        if (!cursor.TryEnterArray())
        {
            return;
        }

        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterObject())
            {
                return;
            }

            string name = string.Empty;
            string value = string.Empty;
            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual("name"u8))
                {
                    name = CdpHeader.ReadString(ref cursor);
                }
                else if (property.SequenceEqual("value"u8))
                {
                    value = CdpHeader.ReadString(ref cursor);
                }
                else if (!cursor.SkipValue())
                {
                    return;
                }
            }

            if (name.Length > 0)
            {
                headers.Add(new CdpHeader(name, value));
            }
        }
    }

    private readonly struct FetchResponseEvent
    {
        public FetchResponseEvent(
            string requestId,
            int statusCode,
            string resourceType,
            IReadOnlyList<CdpHeader> headers)
        {
            RequestId = requestId;
            StatusCode = statusCode;
            ResourceType = resourceType;
            Headers = headers;
        }

        public string RequestId { get; }

        public int StatusCode { get; }

        public string ResourceType { get; }

        public IReadOnlyList<CdpHeader> Headers { get; }
    }

    private async ValueTask DisableFetchAsync(CancellationToken cancellationToken)
    {
        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fetchEnabled)
            {
                return;
            }

            try
            {
                await _session.SendAsync<Unit>("Fetch.disable"u8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fetchEnabled = false;
                _fetchAuth = false;
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private PageRequest? RememberRequest(PageRequest request)
    {
        lock (_requestGate)
        {
            if (_requests.Count >= 4096)
            {
                _requests.Clear();
            }

            if (_observedRequests.Count >= 4096)
            {
                _observedRequests.RemoveRange(0, 1024);
            }

            _observedRequests.Add(request);
            _requests.Remove(request.RequestId, out PageRequest? previous);
            _requests[request.RequestId] = request;
            return previous;
        }
    }

    private static bool TryReadRequestId(ReadOnlySpan<byte> paramsUtf8, out string requestId)
    {
        requestId = string.Empty;
        ZeroCdp.Json.JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
                return requestId.Length > 0;
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }

}
