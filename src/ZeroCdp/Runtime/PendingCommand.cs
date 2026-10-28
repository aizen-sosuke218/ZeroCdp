using System.Threading.Tasks.Sources;
using ZeroCdp.Protocol;

namespace ZeroCdp.Runtime;

/// <summary>Non-generic handle used by the dispatcher so the table needs no type knowledge.</summary>
internal abstract class PendingCommand
{
    public long Id { get; protected set; }

    /// <summary>Monotonic deadline in <see cref="Environment.TickCount64"/> units; 0 means none.</summary>
    public long Deadline { get; set; }

    /// <summary>Cancellation hook, owned and disposed by the slot itself.</summary>
    public CancellationTokenRegistration Registration { get; set; }

    public abstract void Complete(ReadOnlySpan<byte> resultUtf8);

    public abstract void Fail(int code, string message, string? errorData);

    public abstract void Abort(CdpException reason);

    public abstract void Cancel();

    public abstract void Timeout();
}

/// <summary>
/// One in-flight command. Slots are pooled per result type, so a steady command stream does not
/// allocate: the <see cref="ManualResetValueTaskSourceCore{T}"/> makes the await itself free.
/// </summary>
internal sealed class PendingCommand<T> : PendingCommand, IValueTaskSource<T>, IValueTaskSource
    where T : struct, ICdpResult<T>
{
    private static readonly Stack<PendingCommand<T>> Free = new();
    private static readonly Lock Gate = new();

    private ManualResetValueTaskSourceCore<T> _core;
    private FrameDispatcher? _owner;
    private bool _released;
    private int _completed;

    private PendingCommand(bool runContinuationsAsynchronously)
    {
        _core = new ManualResetValueTaskSourceCore<T>
        {
            RunContinuationsAsynchronously = runContinuationsAsynchronously,
        };
    }

    public static PendingCommand<T> Rent(bool runContinuationsAsynchronously)
    {
        lock (Gate)
        {
            if (Free.TryPop(out PendingCommand<T>? existing))
            {
                return existing;
            }
        }

        return new PendingCommand<T>(runContinuationsAsynchronously);
    }

    public void Prepare(long id, FrameDispatcher owner)
    {
        Id = id;
        Deadline = 0;
        _released = false;
        _owner = owner;
        Volatile.Write(ref _completed, 0);
        _core.Reset();
    }

    public ValueTask<T> AsValueTask() => new(this, _core.Version);

    public override void Complete(ReadOnlySpan<byte> resultUtf8)
    {
        if (!TryComplete())
        {
            return;
        }

        if (T.TryParse(resultUtf8, out T value))
        {
            _core.SetResult(value);
            return;
        }

        _core.SetException(new CdpProtocolException(
            $"The browser returned a result for command {Id} that did not match {typeof(T).Name}. " +
            $"Result: {Preview(resultUtf8)}"));
    }

    public override void Fail(int code, string message, string? errorData)
    {
        if (TryComplete())
        {
            _core.SetException(new CdpCommandException(code, message, errorData));
        }
    }

    public override void Abort(CdpException reason)
    {
        if (TryComplete())
        {
            _core.SetException(reason);
        }
    }

    public override void Cancel()
    {
        if (!TryComplete())
        {
            return;
        }

        // The response may still arrive after the caller gave up on the command. Dropping the
        // entry first keeps a late frame from completing a slot that the pool already reused.
        _owner?.Abandon(Id);
        _core.SetException(new OperationCanceledException("The CDP command was cancelled."));
    }

    public override void Timeout()
    {
        if (TryComplete())
        {
            _core.SetException(new CdpTimeoutException(
                $"CDP command {Id} did not complete before its timeout elapsed."));
        }
    }

    /// <summary>
    /// Claims the single completion slot. Completion can race cancellation, timeout and a late
    /// response; without this a second <c>Set*</c> throws on a thread-pool thread and takes the
    /// process down.
    /// </summary>
    private bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;

    private static string Preview(ReadOnlySpan<byte> resultUtf8)
    {
        const int Max = 512;
        int length = Math.Min(resultUtf8.Length, Max);
        string text = System.Text.Encoding.UTF8.GetString(resultUtf8[..length]);
        return resultUtf8.Length > Max ? text + "…" : text;
    }

    T IValueTaskSource<T>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            Release();
        }
    }

    void IValueTaskSource.GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            Release();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token) => _core.GetStatus(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<T>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    private void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        Id = 0;
        _owner = null;
        Registration.Dispose();
        Registration = default;
        lock (Gate)
        {
            if (Free.Count < 128)
            {
                Free.Push(this);
            }
        }
    }
}
