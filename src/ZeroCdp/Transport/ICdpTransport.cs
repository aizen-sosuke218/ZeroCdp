using System.IO.Pipelines;

namespace ZeroCdp.Transport;

/// <summary>
/// Carries CDP frames between the client and the browser.
/// </summary>
/// <remarks>
/// Frames are UTF-8 JSON terminated by a single NUL byte, matching the DevTools pipe framing.
/// Transports that use message framing natively (WebSocket) strip the terminator on send and
/// re-add it on receive, so the rest of the stack sees one uniform stream.
/// </remarks>
public interface ICdpTransport : IAsyncDisposable
{
    /// <summary>Frames separated by <c>0x00</c>.</summary>
    PipeReader Input { get; }

    /// <summary>Sends one frame, terminator included.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Short description used in diagnostics (never contains credentials).</summary>
    string Describe { get; }
}
