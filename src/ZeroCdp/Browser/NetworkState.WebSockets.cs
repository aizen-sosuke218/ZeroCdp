namespace ZeroCdp.Browser;

internal sealed partial class NetworkState
{
    private void HandleWebSocketCreated(ReadOnlySpan<byte> paramsUtf8)
    {
        if (!WebSocketEventParser.TryParseCreated(paramsUtf8, out string requestId, out string url))
        {
            return;
        }

        WebSocketInfo webSocket = new(requestId, url);
        lock (_webSocketGate)
        {
            if (_webSockets.Count >= 1024)
            {
                _webSockets.Clear();
            }

            _webSockets[requestId] = webSocket;
        }

        WebSocketCreated?.Invoke(webSocket);
    }

    private void HandleWebSocketFrame(ReadOnlySpan<byte> paramsUtf8, bool sent)
    {
        if (!WebSocketEventParser.TryParseFrame(paramsUtf8, out string requestId, out WebSocketFrame frame))
        {
            return;
        }

        WebSocketInfo? webSocket;
        lock (_webSocketGate)
        {
            _webSockets.TryGetValue(requestId, out webSocket);
        }

        if (webSocket is null)
        {
            return;
        }

        if (sent)
        {
            webSocket.RaiseFrameSent(frame);
        }
        else
        {
            webSocket.RaiseFrameReceived(frame);
        }
    }

    private void HandleWebSocketFrameError(ReadOnlySpan<byte> paramsUtf8)
    {
        if (!WebSocketEventParser.TryParseFrameError(
                paramsUtf8,
                out string requestId,
                out WebSocketFrameError error))
        {
            return;
        }

        WebSocketInfo? webSocket;
        lock (_webSocketGate)
        {
            _webSockets.TryGetValue(requestId, out webSocket);
        }

        webSocket?.RaiseFrameError(error);
    }

    private void HandleWebSocketClosed(ReadOnlySpan<byte> paramsUtf8)
    {
        if (!WebSocketEventParser.TryParseClosed(paramsUtf8, out string requestId))
        {
            return;
        }

        WebSocketInfo? webSocket;
        lock (_webSocketGate)
        {
            _webSockets.Remove(requestId, out webSocket);
        }

        webSocket?.MarkClosed();
    }
}
