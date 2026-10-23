namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Fetch.enable</c> for the request interception stage.</summary>
public readonly struct EnableFetchParams : ICdpCommandParams
{
    private readonly bool _includeDocumentResponses;
    private readonly bool _handleAuthRequests;

    /// <param name="includeDocumentResponses">
    /// Also pause document responses so a caller can rewrite the HTML before it is parsed. The
    /// request stage stays enabled because routes still need it.
    /// </param>
    /// <param name="handleAuthRequests">
    /// Pause HTTP authentication challenges so context credentials can be supplied without a
    /// browser prompt.
    /// </param>
    public EnableFetchParams(bool includeDocumentResponses = false, bool handleAuthRequests = false)
    {
        _includeDocumentResponses = includeDocumentResponses;
        _handleAuthRequests = handleAuthRequests;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("patterns"u8);
        writer.Value("["u8);
        writer.BeginObject();
        writer.Name("urlPattern"u8);
        writer.StringValue("*");
        writer.Name("requestStage"u8);
        writer.StringValue("Request");
        writer.EndObject();

        if (_includeDocumentResponses)
        {
            writer.Value(","u8);
            writer.BeginObject();
            writer.Name("urlPattern"u8);
            writer.StringValue("*");
            writer.Name("resourceType"u8);
            writer.StringValue("Document");
            writer.Name("requestStage"u8);
            writer.StringValue("Response");
            writer.EndObject();
        }

        writer.Value("]"u8);
        writer.Name("handleAuthRequests"u8);
        writer.BoolValue(_handleAuthRequests);
    }
}

/// <summary>Parameters of <c>Fetch.continueWithAuth</c>.</summary>
public readonly struct ContinueWithAuthParams : ICdpCommandParams
{
    private readonly string _requestId;
    private readonly string _response;
    private readonly string? _username;
    private readonly string? _password;

    private ContinueWithAuthParams(
        string requestId,
        string response,
        string? username,
        string? password)
    {
        _requestId = requestId;
        _response = response;
        _username = username;
        _password = password;
    }

    public static ContinueWithAuthParams ProvideCredentials(
        string requestId,
        string username,
        string password)
        => new(requestId, "ProvideCredentials", username, password);

    public static ContinueWithAuthParams Cancel(string requestId)
        => new(requestId, "CancelAuth", null, null);

    public static ContinueWithAuthParams Default(string requestId)
        => new(requestId, "Default", null, null);

    public void Write(ref CommandWriter writer)
    {
        writer.Name("requestId"u8);
        writer.StringValue(_requestId);
        writer.Name("authChallengeResponse"u8);
        writer.BeginObject();
        writer.Name("response"u8);
        writer.StringValue(_response);
        if (_username is not null)
        {
            writer.Name("username"u8);
            writer.StringValue(_username);
        }
        if (_password is not null)
        {
            writer.Name("password"u8);
            writer.StringValue(_password);
        }
        writer.EndObject();
    }
}

/// <summary>Parameters of <c>Fetch.continueRequest</c>.</summary>
public readonly struct ContinueRequestParams : ICdpCommandParams
{
    private readonly string _requestId;
    private readonly string? _url;
    private readonly string? _method;
    private readonly string? _postData;
    private readonly IReadOnlyList<CdpHeader>? _headers;

    public ContinueRequestParams(
        string requestId,
        string? url = null,
        string? method = null,
        string? postData = null,
        IReadOnlyList<CdpHeader>? headers = null)
    {
        _requestId = requestId;
        _url = url;
        _method = method;
        _postData = postData;
        _headers = headers;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("requestId"u8);
        writer.StringValue(_requestId);

        if (!string.IsNullOrEmpty(_url))
        {
            writer.Name("url"u8);
            writer.StringValue(_url);
        }

        if (!string.IsNullOrEmpty(_method))
        {
            writer.Name("method"u8);
            writer.StringValue(_method);
        }

        if (_postData is not null)
        {
            writer.Name("postData"u8);
            writer.StringValue(_postData);
        }

        if (_headers is not null)
        {
            CdpHeader.WriteArray(ref writer, "headers"u8, _headers);
        }
    }
}

/// <summary>Parameters of <c>Fetch.fulfillRequest</c>.</summary>
public readonly struct FulfillRequestParams : ICdpCommandParams
{
    private readonly string _requestId;
    private readonly int _responseCode;
    private readonly string? _responsePhrase;
    private readonly IReadOnlyList<CdpHeader> _headers;
    private readonly string _bodyBase64;

    public FulfillRequestParams(
        string requestId,
        int responseCode,
        IReadOnlyList<CdpHeader> headers,
        string bodyBase64,
        string? responsePhrase = null)
    {
        _requestId = requestId;
        _responseCode = responseCode;
        _headers = headers;
        _bodyBase64 = bodyBase64;
        _responsePhrase = responsePhrase;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("requestId"u8);
        writer.StringValue(_requestId);
        writer.Name("responseCode"u8);
        writer.Int32Value(_responseCode);

        if (!string.IsNullOrEmpty(_responsePhrase))
        {
            writer.Name("responsePhrase"u8);
            writer.StringValue(_responsePhrase);
        }

        CdpHeader.WriteArray(ref writer, "responseHeaders"u8, _headers);
        writer.Name("body"u8);
        writer.StringValue(_bodyBase64);
    }
}

/// <summary>Parameters of <c>Fetch.failRequest</c>.</summary>
public readonly struct FailRequestParams : ICdpCommandParams
{
    private readonly string _requestId;
    private readonly string _errorReason;

    public FailRequestParams(string requestId, string errorReason)
    {
        _requestId = requestId;
        _errorReason = errorReason;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("requestId"u8);
        writer.StringValue(_requestId);
        writer.Name("errorReason"u8);
        writer.StringValue(_errorReason);
    }
}

/// <summary>Parameters of <c>Network.setCacheDisabled</c>.</summary>
public readonly struct SetCacheDisabledParams : ICdpCommandParams
{
    private readonly bool _cacheDisabled;

    public SetCacheDisabledParams(bool cacheDisabled) => _cacheDisabled = cacheDisabled;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("cacheDisabled"u8);
        writer.BoolValue(_cacheDisabled);
    }
}

/// <summary>Parameters of <c>Network.emulateNetworkConditions</c>.</summary>
public readonly struct EmulateNetworkConditionsParams : ICdpCommandParams
{
    private readonly bool _offline;
    private readonly double _latency;
    private readonly double _downloadThroughput;
    private readonly double _uploadThroughput;
    private readonly string? _browserContextId;

    public EmulateNetworkConditionsParams(
        bool offline,
        double latency = 0,
        double downloadThroughput = -1,
        double uploadThroughput = -1,
        string? browserContextId = null)
    {
        _offline = offline;
        _latency = latency;
        _downloadThroughput = downloadThroughput;
        _uploadThroughput = uploadThroughput;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("offline"u8);
        writer.BoolValue(_offline);
        writer.Name("latency"u8);
        writer.DoubleValue(_latency);
        writer.Name("downloadThroughput"u8);
        writer.DoubleValue(_downloadThroughput);
        writer.Name("uploadThroughput"u8);
        writer.DoubleValue(_uploadThroughput);

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}
