using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Runtime;

/// <summary>Receives CDP events.</summary>
public interface ICdpEventSink
{
    /// <summary>
    /// Called on the connection's reader thread. All spans are only valid for the duration of
    /// the call; copy anything you need to keep.
    /// </summary>
    void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8);
}

/// <summary>
/// Turns one raw frame into either a completed command or an event.
/// </summary>
/// <remarks>
/// The envelope is scanned once and the <c>result</c>/<c>params</c> members are handed to the
/// consumer as slices of the receive buffer. No intermediate object is created and nothing is
/// materialised as a string, which is what makes the steady state allocation free.
/// <para>
/// The pending table is touched from the reader thread, from callers sending commands and from the
/// timeout wheel, so every access is serialised by a lock. Continuations are completed outside
/// that lock: a continuation may send another command, and running it while holding the lock
/// would turn a fast path into a deadlock risk.
/// </para>
/// </remarks>
internal sealed class FrameDispatcher
{
    private readonly Dictionary<long, PendingCommand> _pending = new(64);
    private readonly Lock _gate = new();
    private readonly bool _runContinuationsAsynchronously;
    private readonly int _maxPendingCommands;
    private readonly long _commandTimeoutMs;

    public FrameDispatcher(bool runContinuationsAsynchronously)
        : this(runContinuationsAsynchronously, maxPendingCommands: 0, commandTimeoutMs: 0)
    {
    }

    public FrameDispatcher(bool runContinuationsAsynchronously, int maxPendingCommands, long commandTimeoutMs)
    {
        _runContinuationsAsynchronously = runContinuationsAsynchronously;
        _maxPendingCommands = maxPendingCommands;
        _commandTimeoutMs = commandTimeoutMs;
    }

    public ICdpEventSink? EventSink { get; set; }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public PendingCommand<T> Register<T>(long id)
        where T : struct, ICdpResult<T>
    {
        lock (_gate)
        {
            if (_maxPendingCommands > 0 && _pending.Count >= _maxPendingCommands)
            {
                throw new CdpResourceLimitException(
                    $"The connection already has {_pending.Count} commands in flight; " +
                    $"the limit is {_maxPendingCommands}.");
            }

            PendingCommand<T> slot = PendingCommand<T>.Rent(_runContinuationsAsynchronously);
            slot.Prepare(id);
            slot.Deadline = _commandTimeoutMs > 0
                ? Environment.TickCount64 + _commandTimeoutMs
                : 0;
            _pending.Add(id, slot);
            return slot;
        }
    }

    /// <summary>Drops a command that could not be sent.</summary>
    public void Abandon(long id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    /// <summary>Fails every in-flight command; used when the transport closes.</summary>
    public void AbortAll(CdpException reason)
    {
        PendingCommand[] commands;
        lock (_gate)
        {
            commands = new PendingCommand[_pending.Count];
            _pending.Values.CopyTo(commands, 0);
            _pending.Clear();
        }

        foreach (PendingCommand command in commands)
        {
            command.Abort(reason);
        }
    }

    /// <summary>
    /// Fails commands whose deadline has passed. Called by the connection's timeout wheel; this is
    /// a no-op when no timeout is configured.
    /// </summary>
    public void ExpireStale(long nowTicks)
    {
        List<PendingCommand>? expired = null;

        lock (_gate)
        {
            foreach (KeyValuePair<long, PendingCommand> entry in _pending)
            {
                long deadline = entry.Value.Deadline;
                if (deadline == 0 || deadline > nowTicks)
                {
                    continue;
                }

                expired ??= new List<PendingCommand>(4);
                expired.Add(entry.Value);
            }

            if (expired is not null)
            {
                foreach (PendingCommand command in expired)
                {
                    _pending.Remove(command.Id);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (PendingCommand command in expired)
        {
            command.Timeout();
        }
    }

    public void Dispatch(ReadOnlySpan<byte> frame)
    {
        JsonCursor cursor = new(frame);
        if (!cursor.TryEnterObject())
        {
            throw new CdpProtocolException("CDP frame is not a JSON object.");
        }

        long id = -1;
        bool hasId = false;
        bool hasResult = false;
        bool hasError = false;
        ReadOnlySpan<byte> result = default;
        ReadOnlySpan<byte> error = default;
        ReadOnlySpan<byte> method = default;
        ReadOnlySpan<byte> sessionId = default;
        ReadOnlySpan<byte> parameters = default;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("id"u8))
            {
                hasId = cursor.TryGetInt64(out id);
            }
            else if (name.SequenceEqual("result"u8))
            {
                int start = cursor.Position;
                bool ok = cursor.SkipValue();
                result = ok ? frame[start..cursor.Position] : default;
                hasResult = ok;
            }
            else if (name.SequenceEqual("error"u8))
            {
                int start = cursor.Position;
                bool ok = cursor.SkipValue();
                error = ok ? frame[start..cursor.Position] : default;
                hasError = ok;
            }
            else if (name.SequenceEqual("method"u8))
            {
                if (!cursor.TryReadString(out method, out _))
                {
                    throw new CdpProtocolException("CDP frame has a malformed 'method'.");
                }
            }
            else if (name.SequenceEqual("sessionId"u8))
            {
                cursor.TryReadString(out sessionId, out _);
            }
            else if (name.SequenceEqual("params"u8))
            {
                int start = cursor.Position;
                if (cursor.SkipValue())
                {
                    parameters = frame[start..cursor.Position];
                }
            }
            else if (!cursor.SkipValue())
            {
                throw new CdpProtocolException("CDP frame has a malformed member.");
            }
        }

        if (hasId && (hasResult || hasError))
        {
            PendingCommand? command;
            lock (_gate)
            {
                if (!_pending.Remove(id, out command))
                {
                    // A response for a command we already abandoned or timed out: ignore it,
                    // Chrome does the same.
                    return;
                }
            }

            if (hasError)
            {
                ReadError(error, out int code, out string message, out string? data);
                command.Fail(code, message, data);
            }
            else
            {
                command.Complete(result);
            }

            return;
        }

        if (!method.IsEmpty)
        {
            EventSink?.OnEvent(sessionId, method, parameters);
        }
    }

    private static void ReadError(ReadOnlySpan<byte> errorUtf8, out int code, out string message, out string? data)
    {
        code = 0;
        message = "The CDP command failed.";
        data = null;

        JsonCursor cursor = new(errorUtf8);
        if (!cursor.TryEnterObject())
        {
            return;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("code"u8))
            {
                cursor.TryGetInt32(out code);
            }
            else if (name.SequenceEqual("message"u8))
            {
                message = ReadString(ref cursor) ?? message;
            }
            else if (name.SequenceEqual("data"u8))
            {
                data = ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return;
            }
        }
    }

    private static string? ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return null;
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : null;
    }
}
