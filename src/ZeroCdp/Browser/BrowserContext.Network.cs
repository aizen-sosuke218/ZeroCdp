using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class BrowserContext
{
    /// <summary>
    /// Raised on the reader thread for requests observed in any page of this context, including
    /// pages opened later. Network observation is enabled for the subscribing pages.
    /// </summary>
    public event Action<PageRequest>? Request
    {
        add
        {
            lock (_pageEventGate)
            {
                bool first = _requestStarted is null;
                _requestStarted += value;
                if (first)
                {
                    foreach (CdpPage page in Pages)
                    {
                        if (!page.IsClosed)
                        {
                            page.Request += _forwardRequest;
                        }
                    }
                }
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _requestStarted -= value;
                if (_requestStarted is null)
                {
                    foreach (CdpPage page in Pages)
                    {
                        page.Request -= _forwardRequest;
                    }
                }
            }
        }
    }

    /// <summary>Raised on the reader thread for responses observed in any page of this context.</summary>
    public event Action<PageResponse>? Response
    {
        add
        {
            lock (_pageEventGate)
            {
                bool first = _responseReceived is null;
                _responseReceived += value;
                if (first)
                {
                    foreach (CdpPage page in Pages)
                    {
                        if (!page.IsClosed)
                        {
                            page.Response += _forwardResponse;
                        }
                    }
                }
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _responseReceived -= value;
                if (_responseReceived is null)
                {
                    foreach (CdpPage page in Pages)
                    {
                        page.Response -= _forwardResponse;
                    }
                }
            }
        }
    }

    /// <summary>Raised on the reader thread when a request fails in any page of this context.</summary>
    public event Action<RequestFailure>? RequestFailed
    {
        add
        {
            lock (_pageEventGate)
            {
                bool first = _requestFailed is null;
                _requestFailed += value;
                if (first)
                {
                    foreach (CdpPage page in Pages)
                    {
                        if (!page.IsClosed)
                        {
                            page.RequestFailed += _forwardFailure;
                        }
                    }
                }
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _requestFailed -= value;
                if (_requestFailed is null)
                {
                    foreach (CdpPage page in Pages)
                    {
                        page.RequestFailed -= _forwardFailure;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Raised on the reader thread when any page in this context opens a WebSocket, including
    /// pages opened later. Subscribing enables Network-domain observation for those pages.
    /// </summary>
    public event Action<WebSocketInfo>? WebSocketCreated
    {
        add
        {
            lock (_pageEventGate)
            {
                bool first = _webSocketCreated is null;
                _webSocketCreated += value;
                if (first)
                {
                    foreach (CdpPage page in Pages)
                    {
                        if (!page.IsClosed)
                        {
                            page.WebSocketCreated += _forwardWebSocket;
                        }
                    }
                }
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _webSocketCreated -= value;
                if (_webSocketCreated is null)
                {
                    foreach (CdpPage page in Pages)
                    {
                        page.WebSocketCreated -= _forwardWebSocket;
                    }
                }
            }
        }
    }

    /// <summary>Raised on the reader thread when a response finishes loading in any context page.</summary>
    public event Action<PageResponse>? RequestFinished
    {
        add
        {
            lock (_pageEventGate)
            {
                bool first = _requestFinished is null;
                _requestFinished += value;
                if (first)
                {
                    foreach (CdpPage page in Pages)
                    {
                        if (!page.IsClosed)
                        {
                            page.RequestFinished += _forwardFinished;
                        }
                    }
                }
            }
        }
        remove
        {
            lock (_pageEventGate)
            {
                _requestFinished -= value;
                if (_requestFinished is null)
                {
                    foreach (CdpPage page in Pages)
                    {
                        page.RequestFinished -= _forwardFinished;
                    }
                }
            }
        }
    }

    /// <summary>Playwright-style alias for <see cref="WebSocketCreated"/>.</summary>
    public event Action<WebSocketInfo>? WebSocket
    {
        add => WebSocketCreated += value;
        remove => WebSocketCreated -= value;
    }

    /// <summary>Enables Network-domain observation for every page currently in this context.</summary>
    public async ValueTask EnableNetworkAsync(CancellationToken cancellationToken = default)
    {
        foreach (CdpPage page in Pages)
        {
            if (!page.IsClosed)
            {
                await page.EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Waits for the next request matching a URL glob in any page of this context.</summary>
    public async ValueTask<PageRequest> WaitForRequestAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<PageRequest> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PageRequest> handler = request =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, request.Url))
            {
                completion.TrySetResult(request);
            }
        };

        Request += handler;
        try
        {
            await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? DefaultWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No context request matching '{urlPattern ?? "*"}' arrived within "
                + $"{(timeout ?? DefaultWaitTimeout).TotalSeconds:0.##}s.");
        }
        finally
        {
            Request -= handler;
        }
    }

    /// <summary>Waits for the next response matching a URL glob in any page of this context.</summary>
    public async ValueTask<PageResponse> WaitForResponseAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<PageResponse> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PageResponse> handler = response =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, response.Url))
            {
                completion.TrySetResult(response);
            }
        };

        Response += handler;
        try
        {
            await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? DefaultWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No context response matching '{urlPattern ?? "*"}' arrived within "
                + $"{(timeout ?? DefaultWaitTimeout).TotalSeconds:0.##}s.");
        }
        finally
        {
            Response -= handler;
        }
    }

    /// <summary>Waits for the next response that finishes loading in any context page.</summary>
    public async ValueTask<PageResponse> WaitForRequestFinishedAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<PageResponse> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PageResponse> handler = response =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, response.Url))
            {
                completion.TrySetResult(response);
            }
        };

        RequestFinished += handler;
        try
        {
            await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? DefaultWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No finished context request matching '{urlPattern ?? "*"}' arrived within "
                + $"{(timeout ?? DefaultWaitTimeout).TotalSeconds:0.##}s.");
        }
        finally
        {
            RequestFinished -= handler;
        }
    }

    /// <summary>Waits for the next request that fails in any context page.</summary>
    public async ValueTask<RequestFailure> WaitForRequestFailedAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<RequestFailure> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<RequestFailure> handler = failure =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, failure.Url))
            {
                completion.TrySetResult(failure);
            }
        };

        RequestFailed += handler;
        try
        {
            await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? DefaultWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No failed context request matching '{urlPattern ?? "*"}' arrived within "
                + $"{(timeout ?? DefaultWaitTimeout).TotalSeconds:0.##}s.");
        }
        finally
        {
            RequestFailed -= handler;
        }
    }

    /// <summary>Waits for the next WebSocket matching a URL glob in any page of this context.</summary>
    public async ValueTask<WebSocketInfo> WaitForWebSocketAsync(
        string? urlPattern = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<WebSocketInfo> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WebSocketInfo> handler = webSocket =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, webSocket.Url))
            {
                completion.TrySetResult(webSocket);
            }
        };

        WebSocketCreated += handler;
        try
        {
            await EnableNetworkAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? DefaultWaitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No context WebSocket matching '{urlPattern ?? "*"}' arrived within "
                + $"{(timeout ?? DefaultWaitTimeout).TotalSeconds:0.##}s.");
        }
        finally
        {
            WebSocketCreated -= handler;
        }
    }

}
