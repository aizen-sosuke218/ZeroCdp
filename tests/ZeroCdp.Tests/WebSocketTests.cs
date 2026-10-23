using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class WebSocketTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Page_and_context_observe_websocket_lifecycle_and_frames()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using EchoWebSocketServer server = EchoWebSocketServer.Start();
        string file = Path.Combine(_directory, "socket.html");
        File.WriteAllText(
            file,
            $$"""
            <!doctype html>
            <button id="send">send</button>
            <script>
              window.wsReady = false;
              const ws = new WebSocket('{{server.Url}}');
              ws.addEventListener('open', () => { window.wsReady = true; });
              ws.addEventListener('error', () => { window.wsError = 'error'; });
              ws.addEventListener('close', event => { window.wsError = 'close:' + event.code; });
              ws.addEventListener('message', event => { window.wsMessage = event.data; });
              document.getElementById('send').addEventListener('click', () => ws.send('hello'));
            </script>
            """,
            Encoding.UTF8);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        ConcurrentBag<WebSocketInfo> contextSockets = new();
        context.WebSocketCreated += socket => contextSockets.Add(socket);

        CdpPage page = await context.NewPageAsync();
        await page.EnableNetworkAsync();

        ValueTask<WebSocketInfo> wait = page.WaitForWebSocketAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);
        WebSocketInfo socket = await wait;

        Assert.Equal(server.Url, socket.Url);
        Assert.False(socket.IsClosed);

        await page.WaitForFunctionAsync(
            "window.wsReady === true",
            TimeSpan.FromSeconds(5),
            isolatedContext: false);

        ValueTask<WebSocketFrame> sent = socket.WaitForFrameSentAsync(TimeSpan.FromSeconds(10));
        ValueTask<WebSocketFrame> received = socket.WaitForFrameReceivedAsync(TimeSpan.FromSeconds(10));
        await page.Locator("#send").ClickAsync();

        WebSocketFrame sentFrame = await sent;
        WebSocketFrame receivedFrame = await received;
        Assert.Equal(1, sentFrame.Opcode);
        Assert.Equal("hello", sentFrame.Text);
        Assert.Equal(1, receivedFrame.Opcode);
        Assert.Equal("world", receivedFrame.Text);

        await page.WaitForFunctionAsync(
            "window.wsMessage === 'world'",
            isolatedContext: false);
        Assert.Contains(contextSockets, observed => observed.RequestId == socket.RequestId);

        await socket.WaitForClosedAsync(TimeSpan.FromSeconds(10));
        Assert.True(socket.IsClosed);

        await context.DisposeAsync();
        await server.DisposeAsync();
    }


    [Fact]
    public async Task Page_route_web_socket_mocks_frames_without_a_server()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        TaskCompletionSource<string> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteWebSocketAsync(
            "ws://mock.invalid/*",
            route =>
            {
                route.OnMessage(message =>
                {
                    received.TrySetResult(message);
                    _ = ReplyAsync(route, "mock:" + message);
                });
            });

        await page.EvaluateAsync(
            """
            window.wsMessages = [];
            window.wsOpen = false;
            window.ws = new WebSocket('ws://mock.invalid/socket');
            window.ws.onopen = () => { window.wsOpen = true; window.ws.send('hello'); };
            window.ws.onmessage = event => window.wsMessages.push(event.data);
            window.ws.onclose = event => { window.wsClosed = event.code; };
            """,
            isolatedContext: false);

        Assert.Equal(
            "hello",
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await page.WaitForFunctionAsync(
            "window.wsOpen === true && window.wsMessages.length === 1",
            TimeSpan.FromSeconds(10),
            isolatedContext: false);
        EvaluationResult message = await page.EvaluateAsync(
            "window.wsMessages[0]",
            isolatedContext: false);
        Assert.Equal("mock:hello", message.Value.StringValue);

        await page.EvaluateAsync("window.ws.close(1000, 'done')", isolatedContext: false);
        await page.WaitForFunctionAsync(
            "window.wsClosed === 1000",
            TimeSpan.FromSeconds(10),
            isolatedContext: false);
    }

    [Fact]
    public async Task Page_route_web_socket_proxies_frames_to_a_real_server()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using EchoWebSocketServer server = EchoWebSocketServer.Start();
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        await page.RouteWebSocketAsync(
            server.Url + "*",
            async route => await route.ConnectToServerAsync());

        await page.EvaluateAsync(
            $$"""
            window.wsMessages = [];
            window.wsOpen = false;
            window.ws = new WebSocket('{{server.Url}}');
            window.ws.onopen = () => { window.wsOpen = true; window.ws.send('hello'); };
            window.ws.onmessage = event => window.wsMessages.push(event.data);
            """,
            isolatedContext: false);

        await page.WaitForFunctionAsync(
            "window.wsOpen === true && window.wsMessages.length === 1",
            TimeSpan.FromSeconds(10),
            isolatedContext: false);
        EvaluationResult message = await page.EvaluateAsync(
            "window.wsMessages[0]",
            isolatedContext: false);
        Assert.Equal("world", message.Value.StringValue);
    }

    [Fact]
    public async Task Context_route_web_socket_applies_to_a_future_page()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LocalHttpServer server = new()
        {
            Handler = _ => new TestHttpResponse
            {
                Body = """
                    <!doctype html>
                    <script>
                      window.wsMessages = [];
                      window.wsOpen = false;
                      const ws = new WebSocket('ws://mock.invalid/future');
                      ws.onopen = () => { window.wsOpen = true; ws.send('future'); };
                      ws.onmessage = event => window.wsMessages.push(event.data);
                    </script>
                    """,
            },
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        TaskCompletionSource<string> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteWebSocketAsync(
            "ws://mock.invalid/*",
            route => route.OnMessage(message =>
            {
                received.TrySetResult(message);
                _ = ReplyAsync(route, "context:" + message);
            }));

        CdpPage page = await context.NewPageAsync();
        await page.GotoAsync(server.Url("/ws"), WaitUntil.Load);

        Assert.Equal(
            "future",
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await page.WaitForFunctionAsync(
            "window.wsOpen === true && window.wsMessages.length === 1",
            TimeSpan.FromSeconds(10),
            isolatedContext: false);
        Assert.Equal(
            "context:future",
            (await page.EvaluateAsync("window.wsMessages[0]", isolatedContext: false)).Value.StringValue);
    }

    private static async Task ReplyAsync(WebSocketRoute route, string message)
        => await route.SendAsync(message);

    private sealed class EchoWebSocketServer : IAsyncDisposable
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;
        private int _disposed;

        private EchoWebSocketServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _serverTask = Task.Run(RunAsync);
        }

        public int Port { get; }

        public string Url => $"ws://127.0.0.1:{Port}/";

        public static EchoWebSocketServer Start()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new EchoWebSocketServer(listener);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }

            _cancellation.Dispose();
        }

        [SuppressMessage(
            "Security",
            "CA5350",
            Justification = "RFC 6455 requires SHA-1 for the opening handshake.")]
        private async Task RunAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                string headers = await ReadHeadersAsync(stream, _cancellation.Token).ConfigureAwait(false);
                string key = ReadHeader(headers, "Sec-WebSocket-Key");
                string accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

                string response = "HTTP/1.1 101 Switching Protocols\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Connection: Upgrade\r\n"
                    + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream
                    .WriteAsync(Encoding.ASCII.GetBytes(response), _cancellation.Token)
                    .ConfigureAwait(false);

                await ReadFrameAsync(stream, _cancellation.Token).ConfigureAwait(false);
                await WriteTextFrameAsync(stream, "world", _cancellation.Token).ConfigureAwait(false);
                await WriteCloseFrameAsync(stream, _cancellation.Token).ConfigureAwait(false);
                await Task.Delay(100, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (IOException)
            {
                // The page closed its side of the socket.
            }
            catch (SocketException)
            {
                // The listener was stopped.
            }
        }

        private static async Task<string> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            List<byte> bytes = new(1024);
            byte[] one = new byte[1];
            while (bytes.Count < 16 * 1024)
            {
                int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytes.Add(one[0]);
                int count = bytes.Count;
                if (count >= 4
                    && bytes[count - 4] == (byte)'\r'
                    && bytes[count - 3] == (byte)'\n'
                    && bytes[count - 2] == (byte)'\r'
                    && bytes[count - 1] == (byte)'\n')
                {
                    break;
                }
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static string ReadHeader(string headers, string name)
        {
            foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return string.Empty;
        }

        private static async Task<byte[]> ReadFrameAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] header = new byte[2];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

            int length = header[1] & 0x7f;
            if (length == 126)
            {
                byte[] extended = new byte[2];
                await stream.ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
                length = (extended[0] << 8) | extended[1];
            }
            else if (length == 127)
            {
                byte[] extended = new byte[8];
                await stream.ReadExactlyAsync(extended, cancellationToken).ConfigureAwait(false);
                length = (int)Math.Min(
                    ((long)extended[0] << 56)
                        | ((long)extended[1] << 48)
                        | ((long)extended[2] << 40)
                        | ((long)extended[3] << 32)
                        | ((long)extended[4] << 24)
                        | ((long)extended[5] << 16)
                        | ((long)extended[6] << 8)
                        | extended[7],
                    int.MaxValue);
            }

            bool masked = (header[1] & 0x80) != 0;
            byte[] mask = new byte[4];
            if (masked)
            {
                await stream.ReadExactlyAsync(mask, cancellationToken).ConfigureAwait(false);
            }

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i & 3];
                }
            }

            return payload;
        }

        private static async Task WriteTextFrameAsync(
            NetworkStream stream,
            string text,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            if (payload.Length >= 126)
            {
                throw new InvalidOperationException("The test server only writes short frames.");
            }

            byte[] frame = new byte[2 + payload.Length];
            frame[0] = 0x81;
            frame[1] = (byte)payload.Length;
            payload.CopyTo(frame, 2);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteCloseFrameAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] frame = [0x88, 0x02, 0x03, 0xE8];
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }
}
