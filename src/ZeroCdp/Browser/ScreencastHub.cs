using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>
/// Owns the single <c>Page.startScreencast</c> session for a page. Multiple recorders (trace and
/// video) can subscribe at the same time; frames are copied once by the consumer and acknowledged
/// exactly once here.
/// </summary>
internal sealed class ScreencastHub
{
    private readonly CdpSession _session;
    private readonly PageState _state;
    private readonly Lock _gate = new();
    private Consumer[] _consumers = Array.Empty<Consumer>();
    private bool _running;
    private int _nextId;
    private int _width;
    private int _height;

    internal ScreencastHub(CdpSession session, PageState state)
    {
        _session = session;
        _state = state;
    }

    internal async ValueTask<Subscription> SubscribeAsync(
        int width,
        int height,
        Action<ReadOnlyMemory<byte>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Consumer consumer = new(Interlocked.Increment(ref _nextId), callback);
        bool start;
        lock (_gate)
        {
            _consumers = Append(_consumers, consumer);
            start = !_running;
            if (start)
            {
                _running = true;
                _width = width;
                _height = height;
            }
        }

        if (!start)
        {
            return new Subscription(this, consumer.Id);
        }

        _state.ScreencastFrameReceived = OnFrame;
        try
        {
            await _session
                .SendAsync<Unit, StartScreencastParams>(
                    "Page.startScreencast"u8,
                    new StartScreencastParams(_width, _height),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _consumers = Remove(_consumers, consumer.Id);
                if (_consumers.Length == 0)
                {
                    _running = false;
                    _state.ScreencastFrameReceived = null;
                }
            }

            throw;
        }

        return new Subscription(this, consumer.Id);
    }

    internal async ValueTask UnsubscribeAsync(int id)
    {
        bool stop;
        lock (_gate)
        {
            _consumers = Remove(_consumers, id);
            stop = _running && _consumers.Length == 0;
            if (stop)
            {
                _running = false;
            }
        }

        if (!stop)
        {
            return;
        }

        _state.ScreencastFrameReceived = null;
        try
        {
            await _session.SendAsync<Unit>("Page.stopScreencast"u8).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
        {
            // The target may already be gone.
        }
    }

    internal async ValueTask StopAsync()
    {
        Consumer[] consumers;
        lock (_gate)
        {
            consumers = _consumers;
            _consumers = Array.Empty<Consumer>();
            _running = false;
        }

        foreach (Consumer consumer in consumers)
        {
            consumer.Callback = null;
        }

        _state.ScreencastFrameReceived = null;
        try
        {
            await _session.SendAsync<Unit>("Page.stopScreencast"u8).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
        {
        }
    }

    internal void StopInBackground() => _ = StopInBackgroundAsync();

    private async Task StopInBackgroundAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void OnFrame(ScreencastFrameInfo frame)
    {
        Consumer[] consumers = Volatile.Read(ref _consumers);
        foreach (Consumer consumer in consumers)
        {
            Action<ReadOnlyMemory<byte>>? callback = consumer.Callback;
            if (callback is null)
            {
                continue;
            }

            try
            {
                callback(frame.Data);
            }
            catch
            {
                // A recorder must never break the CDP reader loop.
            }
        }

        _ = AcknowledgeAsync(frame.SessionId);
    }

    private async Task AcknowledgeAsync(int sessionId)
    {
        try
        {
            await _session
                .SendAsync<Unit, ScreencastFrameAckParams>(
                    "Page.screencastFrameAck"u8,
                    new ScreencastFrameAckParams(sessionId))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
        {
        }
    }

    private static Consumer[] Append(Consumer[] current, Consumer consumer)
    {
        Consumer[] next = new Consumer[current.Length + 1];
        current.CopyTo(next, 0);
        next[^1] = consumer;
        return next;
    }

    private static Consumer[] Remove(Consumer[] current, int id)
    {
        int index = -1;
        for (int i = 0; i < current.Length; i++)
        {
            if (current[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return current;
        }

        if (current.Length == 1)
        {
            return Array.Empty<Consumer>();
        }

        Consumer[] next = new Consumer[current.Length - 1];
        current.AsSpan(0, index).CopyTo(next);
        current.AsSpan(index + 1).CopyTo(next.AsSpan(index));
        return next;
    }

    private sealed class Consumer
    {
        public Consumer(int id, Action<ReadOnlyMemory<byte>> callback)
        {
            Id = id;
            Callback = callback;
        }

        public int Id { get; }

        public Action<ReadOnlyMemory<byte>>? Callback { get; set; }
    }

    internal sealed class Subscription : IAsyncDisposable
    {
        private ScreencastHub? _hub;
        private readonly int _id;

        internal Subscription(ScreencastHub hub, int id)
        {
            _hub = hub;
            _id = id;
        }

        public ValueTask DisposeAsync()
        {
            ScreencastHub? hub = Interlocked.Exchange(ref _hub, null);
            return hub is null ? ValueTask.CompletedTask : hub.UnsubscribeAsync(_id);
        }
    }
}
