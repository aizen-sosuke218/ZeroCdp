using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Runtime;

/// <summary>
/// Demultiplexes the single CDP stream into per-target sessions.
/// </summary>
/// <remarks>
/// Every frame that carries a <c>sessionId</c> is matched against a span-keyed table and handed to
/// the owning <see cref="CdpSession"/>; frames without one are browser-level events. The lookup
/// hashes the raw UTF-8 bytes, so routing never allocates.
/// </remarks>
internal sealed class SessionRouter : ICdpEventSink
{
    private readonly CdpConnection _connection;
    private readonly SessionTable _sessions = new();
    private readonly Dictionary<string, CdpSession> _unclaimed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<CdpSession>> _waiters = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SessionRouter(CdpConnection connection) => _connection = connection;

    /// <summary>Receives events that are not scoped to a target.</summary>
    public ICdpEventSink? RootSink { get; set; }

    public Action<CdpSession>? SessionAttached { get; set; }

    public Action<CdpSession>? SessionDetached { get; set; }

    public int Count => _sessions.Count;

    public CdpSession[] Snapshot() => _sessions.Snapshot();

    public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
    {
        if (method.SequenceEqual("Target.attachedToTarget"u8))
        {
            HandleAttached(paramsUtf8, sessionId);
            return;
        }

        if (method.SequenceEqual("Target.detachedFromTarget"u8))
        {
            HandleDetached(paramsUtf8);
            return;
        }

        if (sessionId.IsEmpty)
        {
            RootSink?.OnEvent(sessionId, method, paramsUtf8);
            return;
        }

        if (_sessions.TryGet(sessionId, out CdpSession? session) && session is not null)
        {
            session.Dispatch(method, paramsUtf8);
        }
    }

    /// <summary>
    /// Returns the session for <paramref name="targetId"/>, waiting for the attach event if it has
    /// not arrived yet. Auto-attach and the <c>createTarget</c> response race each other, so both
    /// orders have to work.
    /// </summary>
    public async ValueTask<CdpSession> WaitForSessionAsync(
        string targetId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<CdpSession> waiter;
        lock (_gate)
        {
            if (_unclaimed.Remove(targetId, out CdpSession? ready))
            {
                return ready;
            }

            waiter = new TaskCompletionSource<CdpSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[targetId] = waiter;
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpException($"The browser did not attach to target '{targetId}' within {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(targetId);
            }
        }
    }

    private void HandleAttached(ReadOnlySpan<byte> paramsUtf8, ReadOnlySpan<byte> parentSessionIdUtf8)
    {
        if (!AttachedInfo.TryParse(paramsUtf8, out AttachedInfo info))
        {
            return;
        }

        string? parentSessionId = parentSessionIdUtf8.IsEmpty
            ? null
            : Encoding.UTF8.GetString(parentSessionIdUtf8);

        byte[] sessionIdUtf8 = Encoding.UTF8.GetBytes(info.SessionId);
        CdpSession session = new(
            _connection,
            sessionIdUtf8,
            info.TargetId,
            info.TargetType,
            info.Url,
            info.BrowserContextId,
            info.OpenerId,
            parentSessionId);
        _sessions.Add(sessionIdUtf8, session);

        if (info.WaitingForDebugger)
        {
            // The target is paused until we say so. Failing to do this freezes the page.
            Resume(session);
        }

        lock (_gate)
        {
            if (_waiters.Remove(info.TargetId, out TaskCompletionSource<CdpSession>? waiter))
            {
                waiter.TrySetResult(session);
            }
            else
            {
                _unclaimed[info.TargetId] = session;
            }
        }

        SessionAttached?.Invoke(session);
    }

    private void HandleDetached(ReadOnlySpan<byte> paramsUtf8)
    {
        if (!AttachedInfo.TryParseSessionId(paramsUtf8, out string sessionId))
        {
            return;
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(sessionId);
        if (_sessions.TryGet(utf8, out CdpSession? session) && session is not null)
        {
            _sessions.Remove(utf8);
            SessionDetached?.Invoke(session);
        }
    }

    private static async void Resume(CdpSession session)
    {
        try
        {
            await session.SendAsync<Unit>("Runtime.runIfWaitingForDebugger"u8).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or OperationCanceledException or ObjectDisposedException)
        {
            // The target went away before it was resumed; nothing to recover.
        }
    }

    private readonly struct AttachedInfo
    {
        public string SessionId { get; init; }

        public string TargetId { get; init; }

        public string TargetType { get; init; }

        public string? Url { get; init; }

        public string? BrowserContextId { get; init; }

        public string? OpenerId { get; init; }

        public bool WaitingForDebugger { get; init; }

        public static bool TryParseSessionId(ReadOnlySpan<byte> utf8, out string sessionId)
        {
            sessionId = string.Empty;
            JsonCursor cursor = new(utf8);
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
            {
                if (name.SequenceEqual("sessionId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    sessionId = Encoding.UTF8.GetString(raw);
                    return true;
                }

                if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            return false;
        }

        public static bool TryParse(ReadOnlySpan<byte> utf8, out AttachedInfo info)
        {
            info = default;
            JsonCursor cursor = new(utf8);
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            string sessionId = string.Empty;
            string targetId = string.Empty;
            string targetType = string.Empty;
            string? url = null;
            string? browserContextId = null;
            string? openerId = null;
            bool waiting = false;

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
            {
                if (name.SequenceEqual("sessionId"u8))
                {
                    if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                    {
                        return false;
                    }

                    sessionId = Encoding.UTF8.GetString(raw);
                }
                else if (name.SequenceEqual("waitingForDebugger"u8))
                {
                    cursor.TryGetBoolean(out waiting);
                }
                else if (name.SequenceEqual("targetInfo"u8))
                {
                    if (!cursor.TryEnterObject())
                    {
                        return false;
                    }

                    while (cursor.TryNextProperty(out ReadOnlySpan<byte> infoName))
                    {
                        if (infoName.SequenceEqual("targetId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawId, out _))
                        {
                            targetId = Encoding.UTF8.GetString(rawId);
                        }
                        else if (infoName.SequenceEqual("type"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawType, out _))
                        {
                            targetType = Encoding.UTF8.GetString(rawType);
                        }
                        else if (infoName.SequenceEqual("url"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawUrl, out _))
                        {
                            url = Encoding.UTF8.GetString(rawUrl);
                        }
                        else if (infoName.SequenceEqual("browserContextId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawContext, out _))
                        {
                            browserContextId = Encoding.UTF8.GetString(rawContext);
                        }
                        else if (infoName.SequenceEqual("openerId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawOpener, out _))
                        {
                            openerId = Encoding.UTF8.GetString(rawOpener);
                        }
                        else if (!cursor.SkipValue())
                        {
                            return false;
                        }
                    }
                }
                else if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            if (sessionId.Length == 0 || targetId.Length == 0)
            {
                return false;
            }

            info = new AttachedInfo
            {
                SessionId = sessionId,
                TargetId = targetId,
                TargetType = targetType,
                Url = url,
                BrowserContextId = browserContextId,
                OpenerId = openerId,
                WaitingForDebugger = waiting,
            };
            return true;
        }
    }
}
