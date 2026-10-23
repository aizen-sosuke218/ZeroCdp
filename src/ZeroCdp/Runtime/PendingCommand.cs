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
    private bool _released;

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

    public void Prepare(long id)
    {
        Id = id;
        Deadline = 0;
        _released = false;
        _core.Reset();
    }

    public ValueTask<T> AsValueTask() => new(this, _core.Version);

    public override void Complete(ReadOnlySpan<byte> resultUtf8)
    {
        if (T.TryParse(resultUtf8, out T value))
        {
            _core.SetResult(value);
            return;
        }

        _core.SetException(new CdpProtocolException(
            $"The browser returned a result for command {Id} that did not match {typeof(T).Name}."));
    }

    public override void Fail(int code, string message, string? errorData)
        => _core.SetException(new CdpCommandException(code, message, errorData));

    public override void Abort(CdpException reason)
        => _core.SetException(reason);

    public override void Cancel()
        => _core.SetException(new OperationCanceledException("The CDP command was cancelled."));

    public override void Timeout()
        => _core.SetException(new CdpTimeoutException(
            $"CDP command {Id} did not complete before its timeout elapsed."));

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
