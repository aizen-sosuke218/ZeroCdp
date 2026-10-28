namespace ZeroCdp.Browser;

internal sealed partial class NetworkState
{
    public async ValueTask<PageRequest> WaitForRequestAsync(
        string? urlPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<PageRequest> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PageRequest> handler = request =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, request.Url))
            {
                completion.TrySetResult(request);
            }
        };

        RequestStarted += handler;
        try
        {
            await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No request matching '{urlPattern ?? "*"}' arrived within {timeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            RequestStarted -= handler;
        }
    }

    public async ValueTask<PageResponse> WaitForResponseAsync(
        string? urlPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<PageResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PageResponse> handler = response =>
        {
            if (urlPattern is null || UrlGlob.IsMatch(urlPattern, response.Url))
            {
                completion.TrySetResult(response);
            }
        };

        ResponseReceived += handler;
        try
        {
            await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No response matching '{urlPattern ?? "*"}' arrived within {timeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            ResponseReceived -= handler;
        }
    }

    public async ValueTask<PageResponse> WaitForRequestFinishedAsync(
        string? urlPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<PageResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No finished request matching '{urlPattern ?? "*"}' arrived within {timeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            RequestFinished -= handler;
        }
    }

    public async ValueTask<RequestFailure> WaitForRequestFailedAsync(
        string? urlPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No failed request matching '{urlPattern ?? "*"}' arrived within {timeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            RequestFailed -= handler;
        }
    }

    public async ValueTask<WebSocketInfo> WaitForWebSocketAsync(
        string? urlPattern,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No WebSocket matching '{urlPattern ?? "*"}' arrived within {timeout.TotalSeconds:0.##}s.");
        }
        finally
        {
            WebSocketCreated -= handler;
        }
    }
}
