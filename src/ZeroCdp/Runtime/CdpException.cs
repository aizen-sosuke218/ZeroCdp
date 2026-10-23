namespace ZeroCdp;

/// <summary>Base type for failures reported by ZeroCDP.</summary>
public class CdpException : Exception
{
    public CdpException(string message)
        : base(message)
    {
    }

    public CdpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The browser rejected a command; <paramref name="code"/> and <paramref name="data"/> come from CDP.</summary>
public sealed class CdpCommandException : CdpException
{
    public CdpCommandException(int code, string message, string? data)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>CDP error code, for example -32601 for "method not found".</summary>
    public int Code { get; }

    /// <summary>Optional machine readable error payload, usually a stack trace.</summary>
    public string? ErrorData { get; }
}

/// <summary>The transport ended or the browser process died.</summary>
public sealed class CdpConnectionClosedException : CdpException
{
    public CdpConnectionClosedException()
        : base("The CDP connection is closed.")
    {
    }

    public CdpConnectionClosedException(string message)
        : base(message)
    {
    }

    public CdpConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A frame or command did not match the protocol.</summary>
public sealed class CdpProtocolException : CdpException
{
    public CdpProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A command did not complete within <see cref="Runtime.CdpConnectionOptions.CommandTimeout"/>.
/// The response, if it arrives later, is discarded.
/// </summary>
public sealed class CdpTimeoutException : CdpException
{
    public CdpTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A configured resource limit was reached: a frame was too large, too many commands were in
/// flight, or a payload exceeded the depth limit. The connection is kept usable where possible.
/// </summary>
public sealed class CdpResourceLimitException : CdpException
{
    public CdpResourceLimitException(string message)
        : base(message)
    {
    }
}
