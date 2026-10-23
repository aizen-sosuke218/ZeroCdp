namespace ZeroCdp.Browser;

/// <summary>
/// A browser spawned with <c>--remote-debugging-pipe</c>, exposing the two byte streams that carry
/// CDP frames to and from the child process.
/// </summary>
/// <remarks>
/// POSIX maps the streams to child fds 3/4. Windows passes inherited anonymous-pipe handles through
/// <c>--remote-debugging-io-pipes</c>; either way there is no listening debugger socket.
/// </remarks>
internal interface IPipeBrowserProcess : IBrowserProcess
{
    /// <summary>Browser output that CDP responses and events are read from.</summary>
    Stream Input { get; }

    /// <summary>Browser input that CDP commands are written to.</summary>
    Stream Output { get; }

    /// <summary>Short transport description for diagnostics.</summary>
    string TransportDescription { get; }

    /// <summary>
    /// Completes when the browser's redirected stdout/stderr have been drained. It is already
    /// completed when the child inherited the host's console.
    /// </summary>
    Task Drain { get; }
}
