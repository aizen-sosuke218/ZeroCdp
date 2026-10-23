using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>A request observed on a page.</summary>
public sealed class PageRequest
{
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Network request id when the event came from the Network domain.</summary>
    public string? NetworkId { get; init; }

    public string Url { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string ResourceType { get; init; } = string.Empty;

    public string? FrameId { get; init; }

    public string? PostData { get; init; }

    public IReadOnlyList<CdpHeader> Headers { get; init; } = Array.Empty<CdpHeader>();

    /// <summary>URL of the document that initiated the request, when reported by CDP.</summary>
    public string? DocumentUrl { get; init; }

    /// <summary>Loader id of the document that owns the request.</summary>
    public string? LoaderId { get; init; }

    /// <summary>Monotonic time at which the request was sent, in seconds.</summary>
    public double Timestamp { get; init; }

    /// <summary>Wall-clock time at which the request was sent, in seconds since the epoch.</summary>
    public double WallTime { get; init; }

    /// <summary>True when the request was initiated by a user gesture.</summary>
    public bool HasUserGesture { get; init; }

    public bool IsNavigationRequest => ResourceType.Equals("Document", StringComparison.OrdinalIgnoreCase);

    /// <summary>The previous request in a redirect chain, when this request is a redirect.</summary>
    public PageRequest? RedirectedFrom { get; internal set; }

    /// <summary>The next request in a redirect chain, when Chromium followed a redirect.</summary>
    public PageRequest? RedirectedTo { get; internal set; }

    /// <summary>Status code of the redirect response that produced this request.</summary>
    public int? RedirectResponseStatus { get; internal set; }

    private readonly TaskCompletionSource<PageResponse?> _response =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for the response, or returns null when the request fails first.</summary>
    public Task<PageResponse?> ResponseAsync() => _response.Task;

    /// <summary>Approximate transfer sizes; Chromium reports the encoded response size.</summary>
    public async ValueTask<RequestSizes> SizesAsync()
    {
        PageResponse? response = await _response.Task.ConfigureAwait(false);
        return new RequestSizes(0, 0, response?.EncodedDataLength ?? 0, 0);
    }

    internal void MarkResponse(PageResponse response) => _response.TrySetResult(response);

    internal void MarkFailed() => _response.TrySetResult(null);

    /// <summary>Playwright-style header dictionary.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> HeadersAsync()
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(ToDictionary(Headers));

    /// <summary>Playwright-style alias for all request headers.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> AllHeadersAsync()
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(ToDictionary(Headers));

    /// <summary>Request body as a UTF-8 buffer, or an empty buffer when there is no body.</summary>
    public ValueTask<byte[]> PostDataBufferAsync()
        => ValueTask.FromResult(PostData is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(PostData));

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<CdpHeader> headers)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CdpHeader header in headers)
        {
            result[header.Name] = header.Value;
        }

        return result;
    }
}

/// <summary>Transfer sizes for one request, in bytes.</summary>
public readonly struct RequestSizes
{
    public RequestSizes(long requestBodySize, long requestHeadersSize, long responseBodySize, long responseHeadersSize)
    {
        RequestBodySize = requestBodySize;
        RequestHeadersSize = requestHeadersSize;
        ResponseBodySize = responseBodySize;
        ResponseHeadersSize = responseHeadersSize;
    }

    public long RequestBodySize { get; }

    public long RequestHeadersSize { get; }

    public long ResponseBodySize { get; }

    public long ResponseHeadersSize { get; }
}

/// <summary>TLS details for a response, as reported by Chromium.</summary>
public sealed class SecurityDetails
{
    public string Protocol { get; init; } = string.Empty;

    public string KeyExchange { get; init; } = string.Empty;

    public string Cipher { get; init; } = string.Empty;

    public string SubjectName { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public double ValidFrom { get; init; }

    public double ValidTo { get; init; }

    public IReadOnlyList<string> SanList { get; init; } = Array.Empty<string>();
}

/// <summary>A response observed on a page.</summary>
public sealed class PageResponse
{
    public string RequestId { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public int Status { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    /// <summary>The request that produced this response.</summary>
    public PageRequest? Request { get; internal set; }

    public IReadOnlyList<CdpHeader> Headers { get; init; } = Array.Empty<CdpHeader>();

    /// <summary>HTTP protocol reported by Chromium, for example <c>h2</c>.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Security state reported for the response.</summary>
    public string SecurityState { get; init; } = string.Empty;

    /// <summary>TLS certificate and cipher details, when Chromium reports them.</summary>
    public SecurityDetails? SecurityDetails { get; init; }

    /// <summary>True when the response came from the disk cache.</summary>
    public bool FromDiskCache { get; init; }

    /// <summary>True when the response was served by a service worker.</summary>
    public bool FromServiceWorker { get; init; }

    /// <summary>True when the response came from the prefetch cache.</summary>
    public bool FromPrefetchCache { get; init; }

    /// <summary>Encoded response size reported with the response headers.</summary>
    public long EncodedDataLength { get; init; }

    /// <summary>Remote IP address, when reported.</summary>
    public string? RemoteIPAddress { get; init; }

    /// <summary>Remote port, when reported.</summary>
    public int RemotePort { get; init; }

    /// <summary>Connection id, when reported.</summary>
    public int ConnectionId { get; init; }

    /// <summary>True when the connection was reused.</summary>
    public bool ConnectionReused { get; init; }

    /// <summary>CDP response timing phases in seconds, relative to <c>requestTime</c>.</summary>
    public IReadOnlyDictionary<string, double> Timing { get; init; } = EmptyTiming;

    internal static readonly IReadOnlyDictionary<string, double> EmptyTiming =
        new Dictionary<string, double>(0);

    private readonly TaskCompletionSource<PageResponse> _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the response body has finished loading.</summary>
    public Task<PageResponse> FinishedAsync() => _finished.Task;

    /// <summary>Server address reported for the response, or null when Chromium did not report one.</summary>
    public ValueTask<ServerAddr?> ServerAddrAsync()
        => ValueTask.FromResult<ServerAddr?>(
            string.IsNullOrEmpty(RemoteIPAddress)
                ? null
                : new ServerAddr(RemoteIPAddress, RemotePort));

    internal void MarkFinished() => _finished.TrySetResult(this);

    internal void MarkFailed(string message)
        => _finished.TrySetException(new CdpException(message));

    /// <summary>Playwright-style header dictionary.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> HeadersAsync()
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(ToDictionary(Headers));

    /// <summary>Playwright-style alias for all response headers.</summary>
    public ValueTask<IReadOnlyDictionary<string, string>> AllHeadersAsync()
        => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(ToDictionary(Headers));

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<CdpHeader> headers)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (CdpHeader header in headers)
        {
            result[header.Name] = header.Value;
        }

        return result;
    }
}

/// <summary>Remote server address for a response.</summary>
public readonly struct ServerAddr
{
    public ServerAddr(string ipAddress, int port)
    {
        IpAddress = ipAddress;
        Port = port;
    }

    public string IpAddress { get; }

    public int Port { get; }
}

/// <summary>A request that failed before a response was produced.</summary>
public sealed class RequestFailure
{
    public string RequestId { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string ResourceType { get; init; } = string.Empty;

    public string ErrorText { get; init; } = string.Empty;

    public bool Canceled { get; init; }

    /// <summary>Reason Chromium blocked the request, when it did.</summary>
    public string? BlockedReason { get; init; }
}

internal static class NetworkInfoParser
{
    public static bool TryParseRequest(
        ReadOnlySpan<byte> paramsUtf8,
        bool fetchShape,
        out PageRequest request)
    {
        request = new PageRequest();
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string requestId = string.Empty;
        string networkId = string.Empty;
        string resourceType = string.Empty;
        string? frameId = null;
        string url = string.Empty;
        string method = string.Empty;
        string? postData = null;
        string documentUrl = string.Empty;
        string loaderId = string.Empty;
        double timestamp = 0;
        double wallTime = 0;
        bool hasUserGesture = false;
        int redirectStatus = 0;
        List<CdpHeader> headers = new();

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("networkId"u8))
            {
                networkId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("frameId"u8))
            {
                frameId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("resourceType"u8) || property.SequenceEqual("type"u8))
            {
                resourceType = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("documentURL"u8))
            {
                documentUrl = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("loaderId"u8))
            {
                loaderId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("timestamp"u8))
            {
                cursor.TryGetDouble(out timestamp);
            }
            else if (property.SequenceEqual("wallTime"u8))
            {
                cursor.TryGetDouble(out wallTime);
            }
            else if (property.SequenceEqual("hasUserGesture"u8))
            {
                cursor.TryGetBoolean(out hasUserGesture);
            }
            else if (property.SequenceEqual("redirectResponse"u8))
            {
                if (!TryParseRedirectResponseStatus(ref cursor, out redirectStatus))
                {
                    return false;
                }
            }
            else if (property.SequenceEqual("request"u8))
            {
                if (!TryParseRequestObject(ref cursor, ref url, ref method, ref postData, headers))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (requestId.Length == 0)
        {
            return false;
        }

        request = new PageRequest
        {
            RequestId = requestId,
            NetworkId = string.IsNullOrEmpty(networkId) ? null : networkId,
            Url = url,
            Method = method,
            ResourceType = resourceType,
            FrameId = frameId,
            PostData = postData,
            Headers = headers,
            DocumentUrl = string.IsNullOrEmpty(documentUrl) ? null : documentUrl,
            LoaderId = string.IsNullOrEmpty(loaderId) ? null : loaderId,
            Timestamp = timestamp,
            WallTime = wallTime,
            HasUserGesture = hasUserGesture,
            RedirectResponseStatus = redirectStatus == 0 ? null : redirectStatus,
        };
        return true;
    }

    public static bool TryParseResponse(
        ReadOnlySpan<byte> paramsUtf8,
        out PageResponse response)
    {
        response = new PageResponse();
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string requestId = string.Empty;
        string url = string.Empty;
        int status = 0;
        string statusText = string.Empty;
        string mimeType = string.Empty;
        string protocol = string.Empty;
        string securityState = string.Empty;
        bool fromDiskCache = false;
        bool fromServiceWorker = false;
        bool fromPrefetchCache = false;
        long encodedDataLength = 0;
        string? remoteIPAddress = null;
        int remotePort = 0;
        int connectionId = 0;
        bool connectionReused = false;
        Dictionary<string, double>? timing = null;
        SecurityDetails? securityDetails = null;
        List<CdpHeader> headers = new();

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("response"u8))
            {
                if (!TryParseResponseObject(
                        ref cursor,
                        ref url,
                        ref status,
                        ref statusText,
                        ref mimeType,
                        ref protocol,
                        ref securityState,
                        ref fromDiskCache,
                        ref fromServiceWorker,
                        ref fromPrefetchCache,
                        ref encodedDataLength,
                        ref remoteIPAddress,
                        ref remotePort,
                        ref connectionId,
                        ref connectionReused,
                        ref timing,
                        ref securityDetails,
                        headers))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (requestId.Length == 0)
        {
            return false;
        }

        response = new PageResponse
        {
            RequestId = requestId,
            Url = url,
            Status = status,
            StatusText = statusText,
            MimeType = mimeType,
            Headers = headers,
            Protocol = protocol,
            SecurityState = securityState,
            SecurityDetails = securityDetails,
            FromDiskCache = fromDiskCache,
            FromServiceWorker = fromServiceWorker,
            FromPrefetchCache = fromPrefetchCache,
            EncodedDataLength = encodedDataLength,
            RemoteIPAddress = string.IsNullOrEmpty(remoteIPAddress) ? null : remoteIPAddress,
            RemotePort = remotePort,
            ConnectionId = connectionId,
            ConnectionReused = connectionReused,
            Timing = timing is null ? PageResponse.EmptyTiming : timing,
        };
        return true;
    }

    public static bool TryParseFailure(
        ReadOnlySpan<byte> paramsUtf8,
        out RequestFailure failure)
    {
        failure = new RequestFailure();
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string requestId = string.Empty;
        string resourceType = string.Empty;
        string errorText = string.Empty;
        string blockedReason = string.Empty;
        bool canceled = false;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("type"u8))
            {
                resourceType = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("errorText"u8))
            {
                errorText = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("canceled"u8))
            {
                cursor.TryGetBoolean(out canceled);
            }
            else if (property.SequenceEqual("blockedReason"u8))
            {
                blockedReason = CdpHeader.ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (requestId.Length == 0)
        {
            return false;
        }

        failure = new RequestFailure
        {
            RequestId = requestId,
            ResourceType = resourceType,
            ErrorText = errorText,
            Canceled = canceled,
            BlockedReason = string.IsNullOrEmpty(blockedReason) ? null : blockedReason,
        };
        return true;
    }

    private static bool TryParseRedirectResponseStatus(ref JsonCursor cursor, out int status)
    {
        status = 0;
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("status"u8))
            {
                cursor.TryGetInt32(out status);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseRequestObject(
        ref JsonCursor cursor,
        ref string url,
        ref string method,
        ref string? postData,
        List<CdpHeader> headers)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("url"u8))
            {
                url = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("method"u8))
            {
                method = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("postData"u8))
            {
                postData = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("headers"u8))
            {
                if (!CdpHeader.TryParseObject(ref cursor, headers))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseResponseObject(
        ref JsonCursor cursor,
        ref string url,
        ref int status,
        ref string statusText,
        ref string mimeType,
        ref string protocol,
        ref string securityState,
        ref bool fromDiskCache,
        ref bool fromServiceWorker,
        ref bool fromPrefetchCache,
        ref long encodedDataLength,
        ref string? remoteIPAddress,
        ref int remotePort,
        ref int connectionId,
        ref bool connectionReused,
        ref Dictionary<string, double>? timing,
        ref SecurityDetails? securityDetails,
        List<CdpHeader> headers)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("url"u8))
            {
                url = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("status"u8))
            {
                cursor.TryGetInt32(out status);
            }
            else if (property.SequenceEqual("statusText"u8))
            {
                statusText = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("mimeType"u8))
            {
                mimeType = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("protocol"u8))
            {
                protocol = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("securityState"u8))
            {
                securityState = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("fromDiskCache"u8))
            {
                cursor.TryGetBoolean(out fromDiskCache);
            }
            else if (property.SequenceEqual("fromServiceWorker"u8))
            {
                cursor.TryGetBoolean(out fromServiceWorker);
            }
            else if (property.SequenceEqual("fromPrefetchCache"u8))
            {
                cursor.TryGetBoolean(out fromPrefetchCache);
            }
            else if (property.SequenceEqual("encodedDataLength"u8))
            {
                cursor.TryGetInt64(out encodedDataLength);
            }
            else if (property.SequenceEqual("remoteIPAddress"u8))
            {
                remoteIPAddress = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("remotePort"u8))
            {
                cursor.TryGetInt32(out remotePort);
            }
            else if (property.SequenceEqual("connectionId"u8))
            {
                cursor.TryGetInt32(out connectionId);
            }
            else if (property.SequenceEqual("connectionReused"u8))
            {
                cursor.TryGetBoolean(out connectionReused);
            }
            else if (property.SequenceEqual("timing"u8))
            {
                timing = new Dictionary<string, double>(StringComparer.Ordinal);
                if (!TryParseTiming(ref cursor, timing))
                {
                    return false;
                }
            }
            else if (property.SequenceEqual("securityDetails"u8))
            {
                if (cursor.Peek() == JsonTokenKind.Null)
                {
                    if (!cursor.SkipValue())
                    {
                        return false;
                    }
                }
                else if (!TryParseSecurityDetails(ref cursor, out securityDetails))
                {
                    return false;
                }
            }
            else if (property.SequenceEqual("headers"u8))
            {
                if (!CdpHeader.TryParseObject(ref cursor, headers))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseSecurityDetails(
        ref JsonCursor cursor,
        out SecurityDetails? details)
    {
        details = null;
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        string protocol = string.Empty;
        string keyExchange = string.Empty;
        string cipher = string.Empty;
        string subjectName = string.Empty;
        string issuer = string.Empty;
        double validFrom = 0;
        double validTo = 0;
        List<string> sanList = new();

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("protocol"u8))
            {
                protocol = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("keyExchange"u8))
            {
                keyExchange = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("cipher"u8))
            {
                cipher = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("subjectName"u8))
            {
                subjectName = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("issuer"u8))
            {
                issuer = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("validFrom"u8))
            {
                cursor.TryGetDouble(out validFrom);
            }
            else if (property.SequenceEqual("validTo"u8))
            {
                cursor.TryGetDouble(out validTo);
            }
            else if (property.SequenceEqual("sanList"u8))
            {
                if (cursor.TryEnterArray())
                {
                    while (cursor.TryNextArrayElement())
                    {
                        sanList.Add(CdpHeader.ReadString(ref cursor));
                    }
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        details = new SecurityDetails
        {
            Protocol = protocol,
            KeyExchange = keyExchange,
            Cipher = cipher,
            SubjectName = subjectName,
            Issuer = issuer,
            ValidFrom = validFrom,
            ValidTo = validTo,
            SanList = sanList,
        };
        return true;
    }

    private static bool TryParseTiming(ref JsonCursor cursor, Dictionary<string, double> timing)
    {
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (cursor.TryGetDouble(out double value))
            {
                timing[Encoding.UTF8.GetString(property)] = value;
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Matches Playwright-style URL globs without a regular expression engine.</summary>
internal static class UrlGlob
{
    public static bool IsMatch(string pattern, string url)
    {
        int p = 0;
        int u = 0;
        int star = -1;
        int match = 0;

        while (u < url.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == url[u]))
            {
                p++;
                u++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                match = u;
            }
            else if (star >= 0)
            {
                p = star + 1;
                u = ++match;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
