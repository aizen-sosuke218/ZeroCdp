using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>One WebSocket frame observed through the Network domain.</summary>
/// <remarks>
/// <see cref="PayloadData"/> is the raw CDP payload. Text frames expose it through
/// <see cref="Text"/>; binary frames expose the base64 payload decoded through <see cref="Binary"/>.
/// </remarks>
public sealed class WebSocketFrame
{
    internal WebSocketFrame(int opcode, string payloadData)
    {
        Opcode = opcode;
        PayloadData = payloadData;
    }

    /// <summary>WebSocket opcode (1 = text, 2 = binary, 9 = ping, 10 = pong).</summary>
    public int Opcode { get; }

    public bool IsBinary => Opcode == 2;

    /// <summary>Raw payload reported by the DevTools protocol.</summary>
    public string PayloadData { get; }

    /// <summary>Text payload, or an empty string for a binary frame.</summary>
    public string Text => IsBinary ? string.Empty : PayloadData;

    /// <summary>Decoded binary payload, or an empty array for a text frame.</summary>
    public byte[] Binary => IsBinary ? Convert.FromBase64String(PayloadData) : [];
}

/// <summary>A WebSocket frame-level error reported by Chromium.</summary>
public sealed class WebSocketFrameError
{
    internal WebSocketFrameError(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

/// <summary>
/// A WebSocket observed in a page. Instances are created by the Network domain, never by injecting
/// script into the page, so subscribing does not change the page's visible JavaScript surface.
/// </summary>
public sealed class WebSocketInfo
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private int _closed;

    internal WebSocketInfo(string requestId, string url)
    {
        RequestId = requestId;
        Url = url;
    }

    /// <summary>Network-domain request id for this socket.</summary>
    public string RequestId { get; }

    /// <summary>URL used to open the socket.</summary>
    public string Url { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Raised on the reader thread for frames sent by the page.</summary>
    public event Action<WebSocketFrame>? FrameSent;

    /// <summary>Raised on the reader thread for frames received by the page.</summary>
    public event Action<WebSocketFrame>? FrameReceived;

    /// <summary>Raised on the reader thread for frame-level errors.</summary>
    public event Action<WebSocketFrameError>? FrameError;

    /// <summary>Raised on the reader thread when the socket closes.</summary>
    public event Action? Closed;

    public async ValueTask<WebSocketFrame> WaitForFrameSentAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await WaitForFrameAsync(sent: true, timeout, cancellationToken).ConfigureAwait(false);

    public async ValueTask<WebSocketFrame> WaitForFrameReceivedAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => await WaitForFrameAsync(sent: false, timeout, cancellationToken).ConfigureAwait(false);

    public async ValueTask<WebSocketFrameError> WaitForFrameErrorAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = timeout ?? DefaultWaitTimeout;
        TaskCompletionSource<WebSocketFrameError> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WebSocketFrameError> handler = error => completion.TrySetResult(error);

        lock (_gate)
        {
            FrameError += handler;
        }

        try
        {
            return await completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No WebSocket frame error arrived for '{Url}' within {effective.TotalSeconds:0.##}s.");
        }
        finally
        {
            lock (_gate)
            {
                FrameError -= handler;
            }
        }
    }

    public async ValueTask WaitForClosedAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = timeout ?? DefaultWaitTimeout;
        TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action handler = () => completion.TrySetResult(true);

        lock (_gate)
        {
            if (IsClosed)
            {
                return;
            }

            Closed += handler;
        }

        try
        {
            await completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"WebSocket '{Url}' did not close within {effective.TotalSeconds:0.##}s.");
        }
        finally
        {
            lock (_gate)
            {
                Closed -= handler;
            }
        }
    }

    internal void RaiseFrameSent(WebSocketFrame frame)
    {
        Action<WebSocketFrame>? handlers;
        lock (_gate)
        {
            handlers = FrameSent;
        }

        handlers?.Invoke(frame);
    }

    internal void RaiseFrameReceived(WebSocketFrame frame)
    {
        Action<WebSocketFrame>? handlers;
        lock (_gate)
        {
            handlers = FrameReceived;
        }

        handlers?.Invoke(frame);
    }

    internal void RaiseFrameError(WebSocketFrameError error)
    {
        Action<WebSocketFrameError>? handlers;
        lock (_gate)
        {
            handlers = FrameError;
        }

        handlers?.Invoke(error);
    }

    internal void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Action? handlers;
        lock (_gate)
        {
            handlers = Closed;
        }

        handlers?.Invoke();
    }

    private async ValueTask<WebSocketFrame> WaitForFrameAsync(
        bool sent,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = timeout ?? DefaultWaitTimeout;
        TaskCompletionSource<WebSocketFrame> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WebSocketFrame> handler = frame => completion.TrySetResult(frame);

        lock (_gate)
        {
            if (sent)
            {
                FrameSent += handler;
            }
            else
            {
                FrameReceived += handler;
            }
        }

        try
        {
            return await completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"No {(sent ? "sent" : "received")} WebSocket frame arrived for '{Url}' "
                + $"within {effective.TotalSeconds:0.##}s.");
        }
        finally
        {
            lock (_gate)
            {
                if (sent)
                {
                    FrameSent -= handler;
                }
                else
                {
                    FrameReceived -= handler;
                }
            }
        }
    }
}

internal static class WebSocketEventParser
{
    public static bool TryParseCreated(
        ReadOnlySpan<byte> paramsUtf8,
        out string requestId,
        out string url)
    {
        requestId = string.Empty;
        url = string.Empty;

        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("url"u8))
            {
                url = CdpHeader.ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return requestId.Length > 0 && url.Length > 0;
    }

    public static bool TryParseFrame(
        ReadOnlySpan<byte> paramsUtf8,
        out string requestId,
        out WebSocketFrame frame)
    {
        requestId = string.Empty;
        frame = null!;

        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        int opcode = 1;
        string payloadData = string.Empty;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("response"u8))
            {
                if (!cursor.TryEnterObject())
                {
                    return false;
                }

                while (cursor.TryNextProperty(out ReadOnlySpan<byte> responseProperty))
                {
                    if (responseProperty.SequenceEqual("opcode"u8))
                    {
                        cursor.TryGetInt32(out opcode);
                    }
                    else if (responseProperty.SequenceEqual("payloadData"u8))
                    {
                        payloadData = CdpHeader.ReadString(ref cursor);
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

        if (requestId.Length == 0)
        {
            return false;
        }

        frame = new WebSocketFrame(opcode, payloadData);
        return true;
    }

    public static bool TryParseFrameError(
        ReadOnlySpan<byte> paramsUtf8,
        out string requestId,
        out WebSocketFrameError error)
    {
        requestId = string.Empty;
        string message = "WebSocket frame error.";
        error = null!;

        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("errorMessage"u8))
            {
                string parsed = CdpHeader.ReadString(ref cursor);
                if (parsed.Length > 0)
                {
                    message = parsed;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (requestId.Length == 0)
        {
            return false;
        }

        error = new WebSocketFrameError(message);
        return true;
    }

    public static bool TryParseClosed(ReadOnlySpan<byte> paramsUtf8, out string requestId)
    {
        requestId = string.Empty;
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("requestId"u8))
            {
                requestId = CdpHeader.ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return requestId.Length > 0;
    }
}
