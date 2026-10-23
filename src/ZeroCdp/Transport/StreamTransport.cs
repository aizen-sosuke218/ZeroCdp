using System.IO.Pipelines;

namespace ZeroCdp.Transport;

/// <summary>
/// Transport over a pair of byte streams. This is the backing implementation for
/// <c>--remote-debugging-pipe</c>.
/// </summary>
/// <remarks>
/// POSIX browsers use child fds 3/4. Windows browsers receive two inherited anonymous-pipe handles
/// through <c>--remote-debugging-io-pipes</c>. Both shapes are ordinary byte streams once launched.
/// </remarks>
public sealed class StreamTransport : ICdpTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly string _description;
    private bool _disposed;

    public StreamTransport(Stream input, Stream output)
        : this(input, output, "pipe(fd3/fd4)")
    {
    }

    internal StreamTransport(Stream input, Stream output, string description)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(description);
        _input = input;
        _output = output;
        _description = description;
        _reader = PipeReader.Create(input, new StreamPipeReaderOptions(leaveOpen: true, minimumReadSize: 4096));
    }

    public PipeReader Input => _reader;

    public string Describe => _description;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _reader.CompleteAsync().ConfigureAwait(false);
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }
}
