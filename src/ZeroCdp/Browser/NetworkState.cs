using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

internal sealed partial class NetworkState : IDisposable
{
    private readonly CdpSession _session;
    private readonly RouteRegistry? _contextRoutes;
    private readonly Lock _routeGate = new();
    private readonly List<RouteRegistration> _routes = new();
    private readonly Lock _requestGate = new();
    private readonly Dictionary<string, PageRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageResponse> _responses = new(StringComparer.Ordinal);
    private readonly Lock _webSocketGate = new();
    private readonly Dictionary<string, WebSocketInfo> _webSockets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private int _networkEnabled;
    private bool _fetchEnabled;
    private bool _fetchResponseStage;
    private bool _fetchAuth;
    private HttpCredentials? _httpCredentials;
    private Func<string, string>? _documentTransformer;
    private int _inFlight;
    private long _lastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    public NetworkState(CdpSession session, RouteRegistry? contextRoutes = null)
    {
        _session = session;
        _contextRoutes = contextRoutes;
    }

    /// <summary>Requests that have started but not finished or failed.</summary>
    public int InFlightPendingRequests => Math.Max(0, Volatile.Read(ref _inFlight));

    /// <summary>
    /// Waits until no request has been in flight for <paramref name="idle"/>.
    /// </summary>
    /// <remarks>
    /// This necessarily enables the Network domain: without it there is no request lifecycle to
    /// observe. No Runtime or Console domain is touched.
    /// </remarks>
    public async ValueTask WaitForIdleAsync(
        TimeSpan idle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await EnsureNetworkEnabledAsync(cancellationToken).ConfigureAwait(false);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        while (true)
        {
            long last = Volatile.Read(ref _lastActivityTicks);
            bool idleNow = InFlightPendingRequests == 0
                && System.Diagnostics.Stopwatch.GetElapsedTime(last) >= idle;
            if (idleNow)
            {
                return;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= timeout)
            {
                throw new CdpTimeoutException(
                    $"Network did not become idle for {idle.TotalMilliseconds:0}ms within {timeout.TotalSeconds:0.##}s " +
                    $"({InFlightPendingRequests} request(s) in flight).");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private void BeginRequest()
    {
        Interlocked.Increment(ref _inFlight);
        Volatile.Write(ref _lastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void EndRequest()
    {
        if (Interlocked.Decrement(ref _inFlight) < 0)
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }

        Volatile.Write(ref _lastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void Touch()
        => Volatile.Write(ref _lastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());

    public event Action<PageRequest>? RequestStarted;

    public event Action<PageResponse>? ResponseReceived;

    public event Action<RequestFailure>? RequestFailed;

    /// <summary>Raised when a response body has finished loading.</summary>
    public event Action<PageResponse>? RequestFinished;

    public event Action<WebSocketInfo>? WebSocketCreated;

    public async ValueTask EnsureNetworkEnabledAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _networkEnabled) != 0)
        {
            return;
        }

        await _networkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _networkEnabled) != 0)
            {
                return;
            }

            // Network.enable is used for observation only. Runtime.enable and Console.enable stay off:
            // those are the domains that create page-visible execution-context artifacts.
            await _session.SendAsync<Unit>("Network.enable"u8, cancellationToken).ConfigureAwait(false);

            // Start the idle clock now. The timestamp from construction can be minutes old, which
            // would make the first network-idle wait return before a request that starts with the
            // document has had a chance to run.
            Touch();
            Volatile.Write(ref _networkEnabled, 1);
        }
        finally
        {
            _networkGate.Release();
        }
    }


    public void OnEvent(ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
    {
        if (method.SequenceEqual("Network.webSocketCreated"u8))
        {
            HandleWebSocketCreated(paramsUtf8);
        }
        else if (method.SequenceEqual("Network.webSocketFrameSent"u8))
        {
            HandleWebSocketFrame(paramsUtf8, sent: true);
        }
        else if (method.SequenceEqual("Network.webSocketFrameReceived"u8))
        {
            HandleWebSocketFrame(paramsUtf8, sent: false);
        }
        else if (method.SequenceEqual("Network.webSocketFrameError"u8))
        {
            HandleWebSocketFrameError(paramsUtf8);
        }
        else if (method.SequenceEqual("Network.webSocketClosed"u8))
        {
            HandleWebSocketClosed(paramsUtf8);
        }
        else if (method.SequenceEqual("Fetch.requestPaused"u8))
        {
            HandleRequestPaused(paramsUtf8);
        }
        else if (method.SequenceEqual("Fetch.authRequired"u8))
        {
            HandleAuthRequired(paramsUtf8);
        }
        else if (method.SequenceEqual("Network.requestWillBeSent"u8))
        {
            if (NetworkInfoParser.TryParseRequest(paramsUtf8, fetchShape: false, out PageRequest request))
            {
                BeginRequest();
                PageRequest? previous = RememberRequest(request);
                if (previous is not null)
                {
                    request.RedirectedFrom = previous;
                    previous.RedirectedTo = request;
                    lock (_requestGate)
                    {
                        if (_responses.TryGetValue(request.RequestId, out PageResponse? redirectResponse))
                        {
                            request.RedirectResponseStatus = redirectResponse.Status;
                        }
                    }
                }

                RequestStarted?.Invoke(request);
            }
        }
        else if (method.SequenceEqual("Network.responseReceived"u8))
        {
            if (NetworkInfoParser.TryParseResponse(paramsUtf8, out PageResponse response))
            {
                lock (_requestGate)
                {
                    _responses[response.RequestId] = response;
                    if (_requests.TryGetValue(response.RequestId, out PageRequest? request))
                    {
                        response.Request = request;
                        request.MarkResponse(response);
                    }
                }

                ResponseReceived?.Invoke(response);
            }
        }
        else if (method.SequenceEqual("Network.loadingFailed"u8))
        {
            if (NetworkInfoParser.TryParseFailure(paramsUtf8, out RequestFailure failure))
            {
                EndRequest();
                PageRequest? request;
                lock (_requestGate)
                {
                    _requests.Remove(failure.RequestId, out request);
                }

                if (request is not null)
                {
                    failure = new RequestFailure
                    {
                        RequestId = failure.RequestId,
                        Url = request.Url,
                        ResourceType = failure.ResourceType,
                        ErrorText = failure.ErrorText,
                        Canceled = failure.Canceled,
                        BlockedReason = failure.BlockedReason,
                    };
                    request.MarkFailed();
                }

                PageResponse? failedResponse;
                lock (_requestGate)
                {
                    _responses.Remove(failure.RequestId, out failedResponse);
                }

                failedResponse?.MarkFailed(failure.ErrorText);
                RequestFailed?.Invoke(failure);
            }
        }
        else if (method.SequenceEqual("Network.loadingFinished"u8))
        {
            if (TryReadRequestId(paramsUtf8, out string requestId))
            {
                EndRequest();
                PageResponse? response;
                lock (_requestGate)
                {
                    _requests.Remove(requestId);
                    _responses.Remove(requestId, out response);
                }

                response?.MarkFinished();
                if (response is not null)
                {
                    RequestFinished?.Invoke(response);
                }
            }
        }
    }


    public void Dispose()
    {
        List<WebSocketInfo> sockets;
        lock (_webSocketGate)
        {
            sockets = new List<WebSocketInfo>(_webSockets.Values);
            _webSockets.Clear();
        }

        foreach (WebSocketInfo socket in sockets)
        {
            socket.MarkClosed();
        }

        lock (_requestGate)
        {
            foreach (PageResponse response in _responses.Values)
            {
                response.MarkFailed("The page closed before the response finished.");
            }

            _responses.Clear();
        }

        _networkGate.Dispose();
        _fetchGate.Dispose();
    }
}
