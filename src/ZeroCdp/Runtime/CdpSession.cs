using System.Text;
using ZeroCdp.Protocol;

namespace ZeroCdp.Runtime;

/// <summary>
/// One attached target. Commands sent through a session are routed to that target, and events
/// coming back carry the same session id.
/// </summary>
/// <remarks>
/// The session id is kept as UTF-8 bytes so writing it into a command costs nothing; the
/// <see cref="string"/> form is materialised once for diagnostics and logging.
/// </remarks>
public sealed class CdpSession
{
    private readonly CdpConnection _connection;
    private readonly byte[] _sessionIdUtf8;

    internal CdpSession(
        CdpConnection connection,
        ReadOnlySpan<byte> sessionIdUtf8,
        string targetId,
        string targetType,
        string? url = null,
        string? browserContextId = null,
        string? openerId = null,
        string? parentSessionId = null)
    {
        _connection = connection;
        _sessionIdUtf8 = sessionIdUtf8.ToArray();
        SessionId = Encoding.UTF8.GetString(_sessionIdUtf8);
        TargetId = targetId;
        TargetType = targetType;
        Url = url ?? string.Empty;
        BrowserContextId = browserContextId;
        OpenerId = openerId;
        ParentSessionId = parentSessionId;
    }

    /// <summary>
    /// Creates a browser-level session. Commands sent through it carry no session id, which is
    /// exactly how the browser endpoint is addressed over CDP.
    /// </summary>
    internal static CdpSession CreateBrowserSession(CdpConnection connection)
        => new(connection, ReadOnlySpan<byte>.Empty, targetId: string.Empty, targetType: "browser");

    public string SessionId { get; }

    public string TargetId { get; }

    /// <summary>CDP target type: <c>page</c>, <c>iframe</c>, <c>worker</c>, ...</summary>
    public string TargetType { get; }

    /// <summary>URL reported for the target when it attached, or an empty string.</summary>
    public string Url { get; }

    /// <summary>Browser context that owns this target, or null for the default context.</summary>
    public string? BrowserContextId { get; }

    /// <summary>Target id of the page that opened this one, when the browser reports it.</summary>
    public string? OpenerId { get; }

    /// <summary>
    /// Session that auto-attached this target, for example the page that owns an out-of-process
    /// iframe. Null for targets attached directly at the browser endpoint.
    /// </summary>
    public string? ParentSessionId { get; }

    /// <summary>Receives events for this target only. Called on the reader thread.</summary>
    public ICdpEventSink? EventSink { get; set; }

    public CdpConnection Connection => _connection;

    public ValueTask<T> SendAsync<T>(ReadOnlySpan<byte> methodUtf8, CancellationToken cancellationToken = default)
        where T : struct, ICdpResult<T>
        => _connection.SendAsync<T, NoParams>(methodUtf8, default, _sessionIdUtf8, cancellationToken);

    public ValueTask<T> SendAsync<T, TParams>(
        ReadOnlySpan<byte> methodUtf8,
        in TParams parameters,
        CancellationToken cancellationToken = default)
        where T : struct, ICdpResult<T>
        where TParams : struct, ICdpCommandParams
        => _connection.SendAsync<T, TParams>(methodUtf8, parameters, _sessionIdUtf8, cancellationToken);

    /// <summary>Detaches this session from its target.</summary>
    public ValueTask DetachAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionIdUtf8.Length == 0)
        {
            // Browser-level sessions are the root connection; there is nothing to detach.
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        return _connection.DetachSessionAsync(_sessionIdUtf8, cancellationToken);
    }

    internal void Dispatch(ReadOnlySpan<byte> methodUtf8, ReadOnlySpan<byte> paramsUtf8)
        => EventSink?.OnEvent(_sessionIdUtf8, methodUtf8, paramsUtf8);
}
