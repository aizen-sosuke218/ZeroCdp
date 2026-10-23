using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZeroCdp.Tests;

internal sealed class TestHttpRequest
{
    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class TestHttpResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "text/html; charset=utf-8";

    public string Body { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<TestHttpRequest> _requests = new();

    public LocalHttpServer()
        : this(IPAddress.Loopback)
    {
    }

    public LocalHttpServer(IPAddress address)
    {
        Address = address;
        _listener = new TcpListener(address, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public IPAddress Address { get; }

    public Func<TestHttpRequest, TestHttpResponse>? Handler { get; set; }

    /// <summary>When set, the listener speaks TLS with this certificate.</summary>
    public X509Certificate2? Certificate { get; init; }

    public IReadOnlyCollection<TestHttpRequest> Requests => _requests.ToArray();

    public string Url(string path)
        => Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{Address}]:{Port}{path}"
            : $"http://{Address}:{Port}{path}";

    public void ClearRequests()
    {
        while (_requests.TryDequeue(out _))
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (Certificate is not null)
                {
                    SslStream tls = new(stream, leaveInnerStreamOpen: false);
                    await tls.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = Certificate,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            },
                            _cts.Token)
                        .ConfigureAwait(false);
                    stream = tls;
                }

                byte[]? headerBytes = await ReadHeaderAsync(stream).ConfigureAwait(false);
                if (headerBytes is null)
                {
                    return;
                }

                string headerText = Encoding.UTF8.GetString(headerBytes);
                string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
                if (lines.Length == 0)
                {
                    return;
                }

                string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (requestLine.Length < 2)
                {
                    return;
                }

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                string bodyText = string.Empty;
                if (headers.TryGetValue("Content-Length", out string? contentLengthText)
                    && int.TryParse(contentLengthText, out int contentLength)
                    && contentLength > 0)
                {
                    byte[] body = new byte[contentLength];
                    int read = 0;
                    while (read < body.Length)
                    {
                        int n = await stream.ReadAsync(body.AsMemory(read), _cts.Token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    bodyText = Encoding.UTF8.GetString(body, 0, read);
                }

                TestHttpRequest request = new()
                {
                    Method = requestLine[0],
                    Path = requestLine[1],
                    Body = bodyText,
                    Headers = headers,
                };
                _requests.Enqueue(request);

                TestHttpResponse response = Handler?.Invoke(request) ?? DefaultResponse(request);
                await WriteResponseAsync(stream, response).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // The browser closed the socket or the server is shutting down.
            }
        }
    }

    private static TestHttpResponse DefaultResponse(TestHttpRequest request)
    {
        if (request.Path.StartsWith("/redirect", StringComparison.Ordinal))
        {
            return new TestHttpResponse
            {
                Status = 302,
                Body = string.Empty,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Location"] = "/hello",
                },
            };
        }

        return new TestHttpResponse
        {
            Body = $"""
                <!doctype html>
                <html>
                <head><title>hello</title></head>
                <body><h1 id="heading">Hello from {request.Path}</h1></body>
                </html>
                """,
        };
    }

    private static async Task<byte[]?> ReadHeaderAsync(Stream stream)
    {
        // Read exactly through the header terminator. Reading in large chunks would consume the
        // first bytes of a request body and make POST requests wait forever for Content-Length.
        using MemoryStream accumulated = new();
        byte[] single = new byte[1];
        while (accumulated.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(single).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            accumulated.WriteByte(single[0]);
            byte[] current = accumulated.GetBuffer();
            int length = (int)accumulated.Length;
            if (length >= 4
                && current[length - 4] == (byte)'\r'
                && current[length - 3] == (byte)'\n'
                && current[length - 2] == (byte)'\r'
                && current[length - 1] == (byte)'\n')
            {
                return accumulated.ToArray();
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(Stream stream, TestHttpResponse response)
    {
        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        StringBuilder headers = new();
        headers.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
        headers.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        headers.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        headers.Append("Connection: close\r\n");
        foreach ((string name, string value) in response.Headers)
        {
            headers.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        headers.Append("\r\n");
        byte[] headerBytes = Encoding.UTF8.GetBytes(headers.ToString());
        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
