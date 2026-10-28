using System.Text;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;


/// <summary>Options for <see cref="Route.FetchAsync"/>.</summary>
public sealed class RouteFetchOptions
{
    /// <summary>Headers to override on the fetched request. The paused request headers are kept.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Maximum redirects to follow. Null uses the HTTP client default.</summary>
    public int? MaxRedirects { get; init; }

    /// <summary>Extra attempts after a transport failure.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>HTTP method override, for example <c>POST</c>.</summary>
    public string? Method { get; init; }

    /// <summary>Request body override. Defaults to the paused request body.</summary>
    public string? PostData { get; init; }

    /// <summary>Request timeout. Null uses the context default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>URL override. Defaults to the paused request URL.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// A paused request that can be continued, fulfilled or aborted. Exactly one action wins; later
/// calls throw because Chromium has already resumed the request.
/// </summary>
public sealed class Route
{
    private readonly NetworkState _network;
    private readonly string _requestId;
    private readonly Func<Route, ValueTask>[] _chain;
    private int _handled;
    private int _fellBack;
    private int _chainIndex;

    internal Route(NetworkState network, PageRequest request, Func<Route, ValueTask>[]? chain = null)
    {
        _network = network;
        _requestId = request.RequestId;
        _chain = chain ?? [];
        Request = request;
    }

    /// <summary>The paused request, including URL, method, headers and post data.</summary>
    public PageRequest Request { get; }

    public bool Handled => Volatile.Read(ref _handled) != 0;

    /// <summary>Lets the request continue unchanged.</summary>
    public ValueTask ContinueAsync(CancellationToken cancellationToken = default)
    {
        Claim();
        return _network.ContinueAsync(new ContinueRequestParams(_requestId), cancellationToken);
    }

    /// <summary>
    /// Passes the request to the next matching route, and finally to the network when none is left.
    /// </summary>
    /// <remarks>Only one of continue/fulfill/abort/fallback may be used for a request.</remarks>
    public ValueTask FallbackAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _fellBack, 1) != 0)
        {
            throw new InvalidOperationException("This route has already fallen back.");
        }

        return FallbackCoreAsync(cancellationToken);
    }

    private async ValueTask FallbackCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int next = Interlocked.Increment(ref _chainIndex);
            if (next >= _chain.Length)
            {
                await ContinueAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await _chain[next](this).ConfigureAwait(false);
            if (Handled)
            {
                return;
            }
        }
    }

    /// <summary>Lets the request continue with an overridden URL, method, body and/or headers.</summary>
    public ValueTask ContinueAsync(
        string? url,
        string? method = null,
        string? postData = null,
        IReadOnlyList<CdpHeader>? headers = null,
        CancellationToken cancellationToken = default)
    {
        Claim();

        // Fetch.continueRequest takes post data as base64 over JSON; callers pass the readable
        // text just like Playwright's route.continue(postData: ...).
        string? encodedPostData = postData is null
            ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(postData));

        // Header overrides are merged with the paused request. Replacing the whole list would
        // drop Host and Content-Length, which makes Chromium stall the request. A replaced body
        // always needs the merge, because the original Content-Length is now stale.
        IReadOnlyList<CdpHeader>? mergedHeaders = headers is null && postData is null
            ? null
            : MergeHeaders(headers ?? Array.Empty<CdpHeader>());

        return _network.ContinueAsync(
            new ContinueRequestParams(_requestId, url, method, encodedPostData, mergedHeaders),
            cancellationToken);
    }

    /// <summary>Serves a synthetic response without hitting the network.</summary>
    public ValueTask FulfillAsync(
        int status,
        string body,
        string? contentType = "text/plain; charset=utf-8",
        IReadOnlyList<CdpHeader>? headers = null,
        CancellationToken cancellationToken = default)
        => FulfillAsync(
            status,
            Encoding.UTF8.GetBytes(body),
            contentType,
            headers,
            cancellationToken);

    /// <summary>Serves a synthetic binary response without hitting the network.</summary>
    public ValueTask FulfillAsync(
        int status,
        byte[] body,
        string? contentType = "text/plain; charset=utf-8",
        IReadOnlyList<CdpHeader>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (status is < 100 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Claim();

        List<CdpHeader> responseHeaders = headers is null ? new List<CdpHeader>() : new List<CdpHeader>(headers);
        if (!string.IsNullOrEmpty(contentType) && !HasHeader(responseHeaders, "content-type"))
        {
            responseHeaders.Add(new CdpHeader("content-type", contentType));
        }

        string base64 = Convert.ToBase64String(body);
        return _network.FulfillAsync(
            new FulfillRequestParams(_requestId, status, responseHeaders, base64),
            cancellationToken);
    }

    /// <summary>Serves a file as the response body, inferring the content type when none is given.</summary>
    public async ValueTask FulfillFileAsync(
        string path,
        int status = 200,
        string? contentType = null,
        IReadOnlyList<CdpHeader>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] body = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        await FulfillAsync(
                status,
                body,
                contentType ?? ContentTypeFor(path),
                headers,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the paused request through the page's HTTP client and returns the response
    /// without resuming the route. The caller decides whether to fulfill, continue or abort.
    /// </summary>
    public async ValueTask<APIResponse> FetchAsync(
        RouteFetchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RouteFetchOptions();
        APIRequestContext request = _network.ApiRequestProvider?.Invoke()
            ?? throw new CdpException("Route.FetchAsync requires a page-bound HTTP client.");

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (CdpHeader header in Request.Headers)
        {
            headers[header.Name] = header.Value;
        }

        if (options.Headers is not null)
        {
            foreach ((string name, string value) in options.Headers)
            {
                headers[name] = value;
            }
        }

        HttpMethod method = options.Method is null
            ? new HttpMethod(Request.Method)
            : new HttpMethod(options.Method);
        APIRequestOptions requestOptions = new()
        {
            Headers = headers,
            Data = options.PostData ?? Request.PostData,
            MaxRedirects = options.MaxRedirects,
            Timeout = options.Timeout,
            FailOnStatusCode = false,
        };

        string url = options.Url ?? Request.Url;
        int attempts = Math.Max(1, 1 + (options.MaxRetries ?? 0));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await request
                    .SendAsync(method, url, requestOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Serves an API response, copying its status, headers and body.</summary>
    public async ValueTask FulfillAsync(
        APIResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        byte[] body = await response.BodyAsync(cancellationToken).ConfigureAwait(false);
        List<CdpHeader> headers = new(response.Headers.Count);
        foreach ((string name, string value) in response.Headers)
        {
            headers.Add(new CdpHeader(name, value));
        }

        await FulfillAsync(response.Status, body, contentType: null, headers, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };

    /// <summary>Fails the request with a Chromium network error such as <c>Failed</c> or <c>Aborted</c>.</summary>
    public ValueTask AbortAsync(string errorReason = "Failed", CancellationToken cancellationToken = default)
    {
        Claim();
        return _network.FailAsync(new FailRequestParams(_requestId, errorReason), cancellationToken);
    }

    private List<CdpHeader> MergeHeaders(IReadOnlyList<CdpHeader> overrides)
    {
        List<CdpHeader> merged = new(Request.Headers.Count + overrides.Count + 1);
        foreach (CdpHeader header in Request.Headers)
        {
            if (header.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            merged.Add(header);
        }

        foreach (CdpHeader header in overrides)
        {
            // Chromium rejects these as unsafe in Fetch.continueRequest; it derives the framing
            // from the replacement body instead.
            if (header.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Name.Equals(header.Name, StringComparison.OrdinalIgnoreCase))
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Add(header);
        }

        return merged;
    }

    private static bool HasHeader(List<CdpHeader> headers, string name)
    {
        foreach (CdpHeader header in headers)
        {
            if (header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Claim()
    {
        if (Interlocked.Exchange(ref _handled, 1) != 0)
        {
            throw new InvalidOperationException("This route has already been continued, fulfilled or aborted.");
        }
    }
}

/// <summary>Per-page network observation, interception and waiters.</summary>
