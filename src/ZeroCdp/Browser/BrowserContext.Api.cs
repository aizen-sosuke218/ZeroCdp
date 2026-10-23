using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    private APIRequestContext? _apiRequest;

    /// <summary>
    /// An HTTP client bound to this browser context. Cookies set by API responses are written into
    /// the context's cookie jar, and cookies already in the jar are sent with API requests. This is
    /// the same sharing model as Playwright's <c>context.request</c>.
    /// </summary>
    public APIRequestContext APIRequest
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            lock (_settingsGate)
            {
                return _apiRequest ??= ZeroCdp.Browser.APIRequest.CreateContext(
                    CreateAPIRequestOptions(),
                    this,
                    GetApiCookiesAsync,
                    SetApiCookiesAsync);
            }
        }
    }

    internal async ValueTask DisposeApiRequestAsync()
    {
        APIRequestContext? request;
        lock (_settingsGate)
        {
            request = _apiRequest;
            _apiRequest = null;
        }

        if (request is not null)
        {
            await request.DisposeAsync().ConfigureAwait(false);
        }
    }

    private APIRequestNewContextOptions CreateAPIRequestOptions()
    {
        BrowserContextOptions? options = Options;
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (options is not null)
        {
            foreach (CdpHeader header in options.ExtraHttpHeaders)
            {
                headers[header.Name] = header.Value;
            }
        }

        return new APIRequestNewContextOptions
        {
            BaseUrl = options?.BaseUrl,
            ExtraHttpHeaders = headers,
            HttpCredentials = options?.HttpCredentials is { } credentials
                ? new APIRequestHttpCredentials
                {
                    Username = credentials.Username,
                    Password = credentials.Password,
                }
                : null,
            IgnoreHttpsErrors = options?.IgnoreHttpsErrors ?? false,
            Proxy = options?.ProxyServer,
            UserAgent = options?.UserAgent,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private async ValueTask<IReadOnlyList<CdpCookie>> GetApiCookiesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        _ = url;
        CookiesResult cookies = await GetCookiesAsync(cancellationToken).ConfigureAwait(false);
        return cookies.Cookies;
    }

    private async ValueTask SetApiCookiesAsync(
        string url,
        IReadOnlyList<CdpCookie> cookies,
        CancellationToken cancellationToken)
    {
        _ = url;
        await SetCookiesAsync(cookies, cancellationToken).ConfigureAwait(false);
    }
}
