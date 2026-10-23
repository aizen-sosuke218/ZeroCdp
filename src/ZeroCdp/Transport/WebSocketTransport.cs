using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;

namespace ZeroCdp.Transport;

/// <summary>
/// Transport over a loopback WebSocket, used for the port-mode fallback and for attaching to a
/// browser that is already running.
/// </summary>
/// <remarks>
/// Receive messages are re-framed with a NUL terminator into an internal pipe so that
/// <see cref="CdpConnection"/> only ever sees one framing.
/// </remarks>
public sealed class WebSocketTransport : ICdpTransport
{
    private static readonly byte[] NullTerminator = [0];

    private readonly ClientWebSocket _socket;
    private readonly Pipe _pipe = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiveLoop;
    private bool _disposed;

    private WebSocketTransport(ClientWebSocket socket, string describe)
    {
        _socket = socket;
        Describe = describe;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public PipeReader Input => _pipe.Reader;

    public string Describe { get; }

    public static async ValueTask<WebSocketTransport> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsLoopback)
        {
            throw new ArgumentException("CDP must be reached over the loopback interface.", nameof(uri));
        }

        ClientWebSocket socket = new();
        // No Origin header is sent by ClientWebSocket, so Chrome's remote-origins check does not apply.
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        return new WebSocketTransport(socket, $"websocket({uri.Host}:{uri.Port})");
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Length < 1 || frame.Span[^1] != 0)
        {
            throw new ArgumentException("CDP frames must be NUL terminated.", nameof(frame));
        }

        ReadOnlyMemory<byte> payload = frame[..^1];
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        PipeWriter writer = _pipe.Writer;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result = await _socket
                    .ReceiveAsync(buffer.AsMemory(), _lifetime.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.Count > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, result.Count), _lifetime.Token).ConfigureAwait(false);
                }

                if (result.EndOfMessage)
                {
                    await writer.WriteAsync(NullTerminator, _lifetime.Token).ConfigureAwait(false);
                    await writer.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Lifetime requested shutdown.
        }
        catch (WebSocketException)
        {
            // Peer went away; the connection surfaces this as a closed transport.
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Best effort: the socket is going away regardless.
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort drain.
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
