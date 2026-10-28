using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ZeroCdp.Browser;

/// <summary>HTTP credentials used by <see cref="APIRequestContext"/>.</summary>
public sealed class APIRequestHttpCredentials
{
    public required string Username { get; init; }

    public required string Password { get; init; }
}

/// <summary>Options for creating an <see cref="APIRequestContext"/>.</summary>
public sealed class APIRequestNewContextOptions
{
    /// <summary>Base URL prepended to relative request URLs.</summary>
    public string? BaseUrl { get; init; }

    public IReadOnlyDictionary<string, string>? ExtraHttpHeaders { get; init; }

    public APIRequestHttpCredentials? HttpCredentials { get; init; }

    public bool IgnoreHttpsErrors { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Cookies loaded into the request context's cookie container.</summary>
    public StorageState? StorageState { get; init; }

    /// <summary>Proxy URL, for example <c>http://127.0.0.1:8080</c>.</summary>
    public string? Proxy { get; init; }

    public string? UserAgent { get; init; }
}

/// <summary>Options for one API request.</summary>
public sealed class APIRequestOptions
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Query-string parameters appended to the URL.</summary>
    public IReadOnlyDictionary<string, string?>? Params { get; init; }

    /// <summary>
    /// Request body. Supported values are string, byte[], ReadOnlyMemory&lt;byte&gt;, JsonElement
    /// and JsonDocument. Other CLR objects would require reflection-based serialization and are
    /// therefore rejected in NativeAOT builds.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>URL-encoded form fields. Mutually exclusive with Data.</summary>
    public IReadOnlyDictionary<string, string>? Form { get; init; }

    /// <summary>Multipart fields. Mutually exclusive with Data and Form.</summary>
    public IReadOnlyDictionary<string, APIRequestFile>? Multipart { get; init; }

    public TimeSpan? Timeout { get; init; }

    public bool? IgnoreHttpsErrors { get; init; }

    /// <summary>Throw when the response status is outside 2xx and 3xx. Defaults to true.</summary>
    public bool? FailOnStatusCode { get; init; }

    public int? MaxRedirects { get; init; }
}

/// <summary>A file part for multipart API requests.</summary>
public sealed class APIRequestFile
{
    public required string FileName { get; init; }

    public required byte[] Content { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";
}

/// <summary>A response returned by <see cref="APIRequestContext"/>.</summary>
public sealed class APIResponse : IAsyncDisposable
{
    private readonly byte[] _body;
    private readonly IReadOnlyList<Protocol.CdpHeader> _headersArray;

    internal APIResponse(
        HttpStatusCode status,
        string statusText,
        string url,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyList<Protocol.CdpHeader> headersArray,
        byte[] body,
        string? contentEncoding)
    {
        Status = (int)status;
        StatusText = statusText;
        Url = url;
        Headers = headers;
        _headersArray = headersArray;
        _body = body;
        ContentEncoding = contentEncoding;
    }

    public int Status { get; }

    public string StatusText { get; }

    public bool Ok => Status is >= 200 and <= 299;

    public string Url { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? ContentEncoding { get; }

    /// <summary>Playwright-style async accessor for <see cref="Headers"/>.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> HeadersAsync()
        => new(Headers);

    /// <summary>Every response header as name/value pairs, preserving order and duplicates.</summary>
    public ValueTask<IReadOnlyList<Protocol.CdpHeader>> HeadersArrayAsync()
        => new(_headersArray);

    public ValueTask<byte[]> BodyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<byte[]>(_body);
    }

    public ValueTask<JsonElement?> JsonAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_body.Length == 0)
        {
            return new ValueTask<JsonElement?>((JsonElement?)null);
        }

        using JsonDocument document = JsonDocument.Parse(_body);
        return new ValueTask<JsonElement?>(document.RootElement.Clone());
    }

    public ValueTask<string> TextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(Encoding.UTF8.GetString(_body));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Thrown when a request fails its status check.</summary>
public sealed class APIRequestException : CdpException
{
    public APIRequestException(string message, APIResponse response)
        : base(message) => Response = response;

    public APIResponse Response { get; }
}

/// <summary>Creates and owns an HTTP client that is independent of a browser.</summary>
public sealed class APIRequestContext : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly string? _baseUrl;
    private readonly Dictionary<string, string> _headers;
    private readonly TimeSpan _timeout;
    private readonly APIRequestHttpCredentials? _credentials;
    private readonly Func<string, CancellationToken, ValueTask<IReadOnlyList<Protocol.CdpCookie>>>? _cookieProvider;
    private readonly Func<string, IReadOnlyList<Protocol.CdpCookie>, CancellationToken, ValueTask>? _cookieSink;
    private readonly CookieContainer? _cookies;
    private int _disposed;

    internal APIRequestContext(
        HttpClient client,
        string? baseUrl,
        Dictionary<string, string> headers,
        TimeSpan timeout,
        APIRequestHttpCredentials? credentials,
        Func<string, CancellationToken, ValueTask<IReadOnlyList<Protocol.CdpCookie>>>? cookieProvider = null,
        Func<string, IReadOnlyList<Protocol.CdpCookie>, CancellationToken, ValueTask>? cookieSink = null,
        CookieContainer? cookies = null)
    {
        _client = client;
        _baseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl;
        _headers = headers;
        _timeout = timeout;
        _credentials = credentials;
        _cookieProvider = cookieProvider;
        _cookieSink = cookieSink;
        _cookies = cookies;
    }

    public ValueTask<APIResponse> GetAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, url, options, cancellationToken);

    public ValueTask<APIResponse> PostAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, url, options, cancellationToken);

    public ValueTask<APIResponse> PutAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, url, options, cancellationToken);

    public ValueTask<APIResponse> PatchAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Patch, url, options, cancellationToken);

    public ValueTask<APIResponse> DeleteAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Delete, url, options, cancellationToken);

    public ValueTask<APIResponse> HeadAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Head, url, options, cancellationToken);

    public ValueTask<APIResponse> FetchAsync(
        string url,
        APIRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, url, options, cancellationToken);

    public async ValueTask<APIResponse> SendAsync(
        HttpMethod method,
        string url,
        APIRequestOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrEmpty(url);

        options ??= new APIRequestOptions();
        string requestUrl = BuildUrl(url, options.Params);
        using HttpRequestMessage request = new(method, requestUrl);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        foreach ((string name, string value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (options.Headers is { Count: > 0 })
        {
            foreach ((string name, string value) in options.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (_credentials is not null && request.Headers.Authorization is null)
        {
            string basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_credentials.Username + ":" + _credentials.Password));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        if (_cookieProvider is not null)
        {
            IReadOnlyList<Protocol.CdpCookie> cookies = await _cookieProvider(requestUrl, cancellationToken)
                .ConfigureAwait(false);
            string cookieHeader = BuildCookieHeader(cookies, requestUrl);
            if (cookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        request.Content = BuildContent(options);
        TimeSpan timeout = options.Timeout ?? _timeout;
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(timeout);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The API request to '{requestUrl}' timed out after {timeout.TotalMilliseconds:0} ms.");
        }

        using (response)
        {
            byte[] body = await response.Content
                .ReadAsByteArrayAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            List<Protocol.CdpHeader> headersArray = new();
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                string value = string.Join(", ", header.Value);
                headers[header.Key] = value;
                headersArray.Add(new Protocol.CdpHeader(header.Key, value));
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                string value = string.Join(", ", header.Value);
                headers[header.Key] = value;
                headersArray.Add(new Protocol.CdpHeader(header.Key, value));
            }

            if (_cookieSink is not null
                && response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                List<Protocol.CdpCookie> updates = new();
                foreach (string setCookie in setCookies)
                {
                    Protocol.CdpCookie? cookie = ParseSetCookie(setCookie, requestUrl);
                    if (cookie is not null)
                    {
                        updates.Add(cookie);
                    }
                }

                if (updates.Count > 0)
                {
                    await _cookieSink(requestUrl, updates, cancellationToken).ConfigureAwait(false);
                }
            }

            APIResponse result = new(
                response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                response.RequestMessage?.RequestUri?.ToString() ?? requestUrl,
                headers,
                headersArray,
                body,
                response.Content.Headers.ContentEncoding.FirstOrDefault());

            bool fail = options.FailOnStatusCode ?? true;
            if (fail && !result.Ok)
            {
                throw new APIRequestException(
                    $"API request {method} {requestUrl} returned HTTP {result.Status} {result.StatusText}.",
                    result);
            }

            return result;
        }
    }

    /// <summary>
    /// Snapshots cookies known to this request context. A context bound to a browser captures the
    /// browser context's jar; a standalone context captures its own container. There are no
    /// origins because API-only contexts do not have a <c>localStorage</c>.
    /// </summary>
    public async ValueTask<StorageState> StorageStateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IReadOnlyList<Protocol.CdpCookie> cookies;
        if (_cookieProvider is not null)
        {
            cookies = await _cookieProvider(_baseUrl ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (_cookies is not null)
        {
            List<Protocol.CdpCookie> snapshot = new();
            foreach (Cookie cookie in _cookies.GetAllCookies())
            {
                snapshot.Add(new Protocol.CdpCookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain ?? string.Empty,
                    Path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                    Expires = cookie.Expires == DateTime.MinValue
                        ? -1
                        : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds(),
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                });
            }

            cookies = snapshot;
        }
        else
        {
            cookies = Array.Empty<Protocol.CdpCookie>();
        }

        return new StorageState { Cookies = cookies };
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static string BuildCookieHeader(
        IReadOnlyList<Protocol.CdpCookie> cookies,
        string requestUrl)
    {
        if (cookies.Count == 0 || !Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (Protocol.CdpCookie cookie in cookies)
        {
            if (!CookieMatches(cookie, uri))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return builder.ToString();
    }

    private static bool CookieMatches(Protocol.CdpCookie cookie, Uri uri)
    {
        if (string.IsNullOrEmpty(cookie.Name))
        {
            return false;
        }

        if (cookie.Secure && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string domain = cookie.Domain.TrimStart('.');
        if (domain.Length == 0)
        {
            return false;
        }

        string host = uri.Host;
        if (!host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
        return uri.AbsolutePath.StartsWith(path, StringComparison.Ordinal)
            && (path.EndsWith('/') || uri.AbsolutePath.Length == path.Length
                || uri.AbsolutePath[path.Length] == '/');
    }

    private static Protocol.CdpCookie? ParseSetCookie(string header, string requestUrl)
    {
        string[] parts = header.Split(';');
        int equals = parts[0].IndexOf('=');
        if (equals <= 0)
        {
            return null;
        }

        string name = parts[0][..equals].Trim();
        string value = parts[0][(equals + 1)..].Trim();
        if (name.Length == 0)
        {
            return null;
        }

        double expires = -1;
        bool secure = false;
        bool httpOnly = false;
        bool hasMaxAge = false;
        string sameSite = string.Empty;
        string? domain = null;
        string? path = null;
        for (int i = 1; i < parts.Length; i++)
        {
            string attribute = parts[i].Trim();
            int separator = attribute.IndexOf('=');
            string key = separator >= 0 ? attribute[..separator].Trim() : attribute;
            string attributeValue = separator >= 0 ? attribute[(separator + 1)..].Trim() : string.Empty;
            if (key.Equals("secure", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
            }
            else if (key.Equals("httponly", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
            }
            else if (key.Equals("samesite", StringComparison.OrdinalIgnoreCase))
            {
                sameSite = attributeValue;
            }
            else if (key.Equals("domain", StringComparison.OrdinalIgnoreCase))
            {
                domain = attributeValue;
            }
            else if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                path = attributeValue;
            }
            else if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(attributeValue, out long maxAge))
            {
                // Max-Age takes precedence over Expires when both are present.
                hasMaxAge = true;
                expires = maxAge <= 0
                    ? 0
                    : DateTimeOffset.UtcNow.AddSeconds(maxAge).ToUnixTimeSeconds();
            }
            else if (!hasMaxAge
                && key.Equals("expires", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(attributeValue, out DateTimeOffset expiresAt))
            {
                expires = expiresAt.ToUnixTimeSeconds();
            }
        }

        return new Protocol.CdpCookie
        {
            Name = name,
            Value = value,
            Url = requestUrl,
            Domain = domain ?? string.Empty,
            Path = path ?? "/",
            Expires = expires,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = sameSite.ToLowerInvariant() switch
            {
                "strict" => "Strict",
                "lax" => "Lax",
                "none" => "None",
                _ => string.Empty,
            },
        };
    }

    private string BuildUrl(string url, IReadOnlyDictionary<string, string?>? parameters)
    {
        string effective = url;
        if (_baseUrl is not null && Uri.TryCreate(url, UriKind.Relative, out _))
        {
            string baseUrl = _baseUrl.EndsWith('/') ? _baseUrl : _baseUrl + "/";
            effective = new Uri(new Uri(baseUrl), url).ToString();
        }

        if (parameters is not { Count: > 0 })
        {
            return effective;
        }

        StringBuilder builder = new(effective);
        char separator = effective.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        foreach ((string name, string? value) in parameters)
        {
            builder.Append(separator);
            separator = '&';
            builder.Append(Uri.EscapeDataString(name));
            if (value is not null)
            {
                builder.Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return builder.ToString();
    }

    private static HttpContent? BuildContent(APIRequestOptions options)
    {
        int groups = (options.Data is not null ? 1 : 0)
            + (options.Form is { Count: > 0 } ? 1 : 0)
            + (options.Multipart is { Count: > 0 } ? 1 : 0);
        if (groups > 1)
        {
            throw new ArgumentException("Only one of Data, Form or Multipart can be set.");
        }

        if (options.Form is { Count: > 0 })
        {
            return new FormUrlEncodedContent(
                options.Form.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
        }

        if (options.Multipart is { Count: > 0 })
        {
            MultipartFormDataContent multipart = new();
            foreach ((string name, APIRequestFile file) in options.Multipart)
            {
                ByteArrayContent content = new(file.Content);
                content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                multipart.Add(content, name, file.FileName);
            }

            return multipart;
        }

        return options.Data switch
        {
            null => null,
            string text => new StringContent(text, Encoding.UTF8, "text/plain"),
            byte[] bytes => new ByteArrayContent(bytes),
            ReadOnlyMemory<byte> memory => new ByteArrayContent(memory.ToArray()),
            JsonElement element => new StringContent(element.GetRawText(), Encoding.UTF8, "application/json"),
            JsonDocument document => new StringContent(document.RootElement.GetRawText(), Encoding.UTF8, "application/json"),
            _ => throw new ArgumentException(
                "Data must be a string, byte[], ReadOnlyMemory<byte>, JsonElement or JsonDocument. " +
                "Reflection-based object serialization is not available in NativeAOT."),
        };
    }
}

/// <summary>Creates browser-independent API request contexts.</summary>
public static class APIRequest
{
    /// <summary>Creates a new API request context with its own cookie jar.</summary>
    public static ValueTask<APIRequestContext> NewContextAsync(
        APIRequestNewContextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<APIRequestContext>(CreateContext(options, null, null, null));
    }

    internal static APIRequestContext CreateContext(
        APIRequestNewContextOptions? options,
        BrowserContext? browserContext,
        Func<string, CancellationToken, ValueTask<IReadOnlyList<Protocol.CdpCookie>>>? cookieProvider,
        Func<string, IReadOnlyList<Protocol.CdpCookie>, CancellationToken, ValueTask>? cookieSink)
    {
        options ??= new APIRequestNewContextOptions();

        CookieContainer cookies = new();
        if (options.StorageState is { Cookies.Count: > 0 })
        {
            foreach (Protocol.CdpCookie cookie in options.StorageState.Cookies)
            {
                try
                {
                    Cookie value = new(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                    {
                        Secure = cookie.Secure,
                        HttpOnly = cookie.HttpOnly,
                        Expires = cookie.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires).UtcDateTime : DateTime.MinValue,
                    };
                    cookies.Add(value);
                }
                catch (CookieException)
                {
                    // Ignore cookies the .NET cookie container rejects.
                }
            }
        }

        HttpClientHandler handler = new()
        {
            CookieContainer = cookies,
            UseCookies = cookieProvider is null,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };

        if (options.IgnoreHttpsErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        if (!string.IsNullOrEmpty(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }

        HttpClient client = new(handler, disposeHandler: true);
        if (!string.IsNullOrEmpty(options.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (options.ExtraHttpHeaders is { Count: > 0 })
        {
            foreach ((string name, string value) in options.ExtraHttpHeaders)
            {
                headers[name] = value;
            }
        }

        return new APIRequestContext(
            client,
            options.BaseUrl,
            headers,
            options.Timeout ?? TimeSpan.FromSeconds(30),
            options.HttpCredentials,
            cookieProvider,
            cookieSink,
            cookies);
    }
}
