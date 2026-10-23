using System.Buffers;
using System.IO.Pipelines;
using ZeroCdp.Protocol;
using ZeroCdp.Transport;

namespace ZeroCdp.Runtime;

/// <summary>Observes one outbound CDP frame, including its NUL terminator.</summary>
/// <remarks>
/// Used for diagnostics and tests: it can prove which domains a run touched without adding a
/// proxy or a fake transport. The frame is only valid for the duration of the call.
/// </remarks>
public delegate void CdpFrameObserver(ReadOnlySpan<byte> frame);

/// <summary>Tuning knobs for one connection.</summary>
public sealed class CdpConnectionOptions
{
    /// <summary>
    /// Run command continuations on the thread pool instead of inline on the reader thread.
    /// Inline is the default because it avoids a thread hop per command; it requires callers not
    /// to block synchronously inside their continuations.
    /// </summary>
    public bool RunContinuationsAsynchronously { get; init; }

    /// <summary>
    /// Reject any frame larger than this. A hostile page can otherwise make the browser emit
    /// arbitrarily large protocol messages and exhaust our memory.
    /// </summary>
    public int MaxFrameBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// Maximum number of commands waiting for a response. A page cannot flood this on its own,
    /// but calling code can, and an unbounded table is a memory leak with extra steps.
    /// </summary>
    public int MaxPendingCommands { get; init; } = 4096;

    /// <summary>
    /// Deadline for every command. The timeout is enforced by one shared timer loop, so the
    /// command path does not allocate a timer per call. Use <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable it.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum depth requested from <c>DOM.getDocument</c> and <c>DOM.describeNode</c>. The
    /// protocol accepts -1 for "everything", which a deeply nested hostile document can turn into
    /// a very large response; the frame cap is the hard backstop, this is the cheap one.
    /// </summary>
    public int MaxDomDepth { get; init; } = 512;

    /// <summary>
    /// Called for every command written to the transport. Null by default; the branch costs
    /// nothing on the steady-state path when it is unset.
    /// </summary>
    public CdpFrameObserver? CommandObserver { get; init; }

    internal void Validate()
    {
        if (MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameBytes));
        }

        if (MaxPendingCommands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPendingCommands));
        }

        if (MaxDomDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDomDepth));
        }

        if (CommandTimeout != Timeout.InfiniteTimeSpan && CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }
    }
}

/// <summary>
/// A CDP session over one transport: one reader loop, allocation-free frame dispatch, and
/// <see cref="ValueTask{T}"/> commands that do not allocate once warm.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ICdpTransport _transport;
    private readonly FrameDispatcher _dispatcher;
    private readonly CdpConnectionOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerLoop;
    private readonly Task _timeoutLoop;
    private long _nextId;
    private volatile bool _closed;
    private int _disposed;

    private CdpConnection(ICdpTransport transport, CdpConnectionOptions options)
    {
        _transport = transport;
        _options = options;
        long timeoutMs = options.CommandTimeout == Timeout.InfiniteTimeSpan
            ? 0
            : Math.Max(1, (long)options.CommandTimeout.TotalMilliseconds);

        _dispatcher = new FrameDispatcher(
            options.RunContinuationsAsynchronously,
            options.MaxPendingCommands,
            timeoutMs);
        _readerLoop = Task.Run(ReadLoopAsync);
        _timeoutLoop = timeoutMs > 0 ? Task.Run(TimeoutLoopAsync) : Task.CompletedTask;
    }

    public static CdpConnection Create(ICdpTransport transport, CdpConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        CdpConnectionOptions effective = options ?? new CdpConnectionOptions();
        effective.Validate();
        return new CdpConnection(transport, effective);
    }

    /// <summary>Receives events. Set before sending commands that produce them.</summary>
    public ICdpEventSink? EventSink
    {
        get => _dispatcher.EventSink;
        set => _dispatcher.EventSink = value;
    }

    public string TransportDescription => _transport.Describe;

    /// <summary>The validated options this connection was created with.</summary>
    public CdpConnectionOptions Options => _options;

    public bool IsClosed => _closed;

    internal int PendingCount => _dispatcher.PendingCount;

    public ValueTask<T> SendAsync<T>(ReadOnlySpan<byte> methodUtf8, CancellationToken cancellationToken = default)
        where T : struct, ICdpResult<T>
        => SendCoreAsync<T, NoParams>(methodUtf8, default, default, cancellationToken);

    public ValueTask<T> SendAsync<T, TParams>(
        ReadOnlySpan<byte> methodUtf8,
        in TParams parameters,
        CancellationToken cancellationToken = default)
        where T : struct, ICdpResult<T>
        where TParams : struct, ICdpCommandParams
        => SendCoreAsync<T, TParams>(methodUtf8, parameters, default, cancellationToken);

    /// <summary>
    /// Sends a command on an attached target session. CDP routes the command to the target and
    /// tags the response and any resulting events with the same session id.
    /// </summary>
    internal ValueTask<T> SendAsync<T, TParams>(
        ReadOnlySpan<byte> methodUtf8,
        in TParams parameters,
        ReadOnlySpan<byte> sessionIdUtf8,
        CancellationToken cancellationToken)
        where T : struct, ICdpResult<T>
        where TParams : struct, ICdpCommandParams
        => SendCoreAsync<T, TParams>(methodUtf8, parameters, sessionIdUtf8, cancellationToken);

    /// <summary>
    /// Writes the command and hands it to the transport. The common case (a pipe write that
    /// completes immediately) returns without allocating.
    /// </summary>
    private ValueTask<T> SendCoreAsync<T, TParams>(
        ReadOnlySpan<byte> methodUtf8,
        in TParams parameters,
        ReadOnlySpan<byte> sessionIdUtf8,
        CancellationToken cancellationToken)
        where T : struct, ICdpResult<T>
        where TParams : struct, ICdpCommandParams
    {
        if (_closed)
        {
            throw new CdpConnectionClosedException();
        }

        long id = Interlocked.Increment(ref _nextId);
        PendingCommand<T> slot = _dispatcher.Register<T>(id);

        if (_closed)
        {
            // The connection closed between the check above and registration. Without this the
            // command would wait forever because AbortAll has already run.
            _dispatcher.Abandon(id);
            throw new CdpConnectionClosedException();
        }

        if (cancellationToken.CanBeCanceled)
        {
            // Only cancellable callers pay for the registration; the slot disposes it on release.
            slot.Registration = cancellationToken.Register(
                static state => ((PendingCommand)state!).Cancel(),
                slot);
        }

        CommandWriter writer = CommandWriter.Create(256);
        try
        {
            writer.Begin(id, methodUtf8);
            writer.BeginParams();
            parameters.Write(ref writer);
            writer.EndParams();
            if (!sessionIdUtf8.IsEmpty)
            {
                writer.SessionId(sessionIdUtf8);
            }

            ReadOnlyMemory<byte> frame = writer.FinishMemory();

            _options.CommandObserver?.Invoke(frame.Span);

            ValueTask send = _transport.SendAsync(frame, cancellationToken);
            if (send.IsCompletedSuccessfully)
            {
                send.GetAwaiter().GetResult();
                writer.Dispose();
                return slot.AsValueTask();
            }

            return SendSlowAsync(send, writer, slot, id);
        }
        catch
        {
            writer.Dispose();
            _dispatcher.Abandon(id);
            throw;
        }
    }

    private async ValueTask<T> SendSlowAsync<T>(ValueTask send, CommandWriter writer, PendingCommand<T> slot, long id)
        where T : struct, ICdpResult<T>
    {
        try
        {
            await send.ConfigureAwait(false);
        }
        catch
        {
            _dispatcher.Abandon(id);
            throw;
        }
        finally
        {
            writer.Dispose();
        }

        return await slot.AsValueTask().ConfigureAwait(false);
    }

    /// <summary>Detaches a session from its target through the browser endpoint.</summary>
    internal async ValueTask DetachSessionAsync(ReadOnlyMemory<byte> sessionIdUtf8, CancellationToken cancellationToken)
        => await SendAsync<Unit, DetachSessionParams>(
                "Target.detachFromTarget"u8,
                new DetachSessionParams(sessionIdUtf8),
                cancellationToken)
            .ConfigureAwait(false);

    internal void DispatchForTest(ReadOnlySpan<byte> frame) => _dispatcher.Dispatch(frame);

    private async Task ReadLoopAsync()
    {
        PipeReader reader = _transport.Input;
        int maxFrameBytes = _options.MaxFrameBytes;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                ReadResult readResult = await reader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = readResult.Buffer;

                if (buffer.Length > maxFrameBytes && buffer.PositionOf((byte)0) is null)
                {
                    throw new CdpResourceLimitException(
                        $"A CDP frame exceeded the {maxFrameBytes} byte limit; dropping the connection.");
                }

                SequenceReader<byte> sequence = new(buffer);
                while (sequence.TryReadTo(out ReadOnlySequence<byte> frame, (byte)0))
                {
                    if (frame.IsSingleSegment)
                    {
                        _dispatcher.Dispatch(frame.FirstSpan);
                    }
                    else
                    {
                        DispatchFragmented(frame);
                    }
                }

                reader.AdvanceTo(sequence.Position, buffer.End);

                if (readResult.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected on dispose.
        }
        catch (Exception ex)
        {
            _closed = true;
            _dispatcher.AbortAll(new CdpConnectionClosedException("The CDP transport failed.", ex));
            return;
        }

        _closed = true;
        _dispatcher.AbortAll(new CdpConnectionClosedException());
    }

    private async Task TimeoutLoopAsync()
    {
        try
        {
            // One shared wheel for the whole connection. A 100 ms resolution is far below the
            // default 30 s deadline and costs one wake-up per tick, not one timer per command.
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                _dispatcher.ExpireStale(Environment.TickCount64);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected on dispose.
        }
    }

    private void DispatchFragmented(ReadOnlySequence<byte> frame)
    {
        // Rare: only when a frame straddles pipe segments. Copy into a pooled buffer so the
        // dispatcher can keep working on a single span.
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)frame.Length);
        try
        {
            frame.CopyTo(rented);
            _dispatcher.Dispatch(rented.AsSpan(0, (int)frame.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readerLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or CdpConnectionClosedException)
        {
            // Shutdown path.
        }

        try
        {
            await _timeoutLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutdown path.
        }

        _closed = true;
        _dispatcher.AbortAll(new CdpConnectionClosedException());
        _lifetime.Dispose();
    }
}
