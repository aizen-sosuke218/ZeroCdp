using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// Records Network-domain traffic for a browser context and writes a HAR 1.2 file when the
/// context closes. Text bodies are embedded; binary bodies are stored as base64.
/// </summary>
internal sealed class HarRecorder
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<HarEntry> _entries = new();
    private readonly Dictionary<string, HarEntry> _byRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<CdpPage, Binding> _pages = new();
    private readonly List<Task> _captures = new();

    public HarRecorder(string path) => _path = path;

    public void Attach(CdpPage page)
    {
        Binding binding = new(this, page);
        lock (_gate)
        {
            _pages[page] = binding;
        }

        page.Request += binding.OnRequest;
        page.Response += binding.OnResponse;
        page.RequestFailed += binding.OnFailure;
    }

    public void Detach(CdpPage page)
    {
        Binding? binding;
        lock (_gate)
        {
            _pages.Remove(page, out binding);
        }

        if (binding is not null)
        {
            page.Request -= binding.OnRequest;
            page.Response -= binding.OnResponse;
            page.RequestFailed -= binding.OnFailure;
        }
    }

    public async ValueTask WriteAsync(CancellationToken cancellationToken)
    {
        Task[] captures;
        lock (_gate)
        {
            captures = _captures.ToArray();
        }

        if (captures.Length > 0)
        {
            try
            {
                await Task.WhenAll(captures)
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or CdpException or ObjectDisposedException)
            {
                // A page can disappear while its body is being read. The entry is still useful
                // without the body.
            }
        }

        HarEntry[] entries;
        lock (_gate)
        {
            entries = _entries.ToArray();
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = new(
            _path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await using Utf8JsonWriter writer = new(
            stream,
            new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName("log");
        writer.WriteStartObject();
        writer.WriteString("version", "1.2");
        writer.WritePropertyName("creator");
        writer.WriteStartObject();
        writer.WriteString("name", "ZeroCDP");
        writer.WriteString("version", "1.0");
        writer.WriteEndObject();
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (HarEntry entry in entries)
        {
            WriteEntry(writer, entry);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnRequest(PageRequest request)
    {
        lock (_gate)
        {
            HarEntry entry = new(request);
            _entries.Add(entry);
            _byRequestId[request.RequestId] = entry;
        }
    }

    private void OnResponse(CdpPage page, PageResponse response)
    {
        HarEntry? entry;
        lock (_gate)
        {
            _byRequestId.TryGetValue(response.RequestId, out entry);
        }

        if (entry is null)
        {
            return;
        }

        entry.Response = response;
        Task capture = CaptureBodyAsync(page, response, entry);
        lock (_gate)
        {
            _captures.Add(capture);
        }
    }

    private void OnFailure(RequestFailure failure)
    {
        lock (_gate)
        {
            if (_byRequestId.TryGetValue(failure.RequestId, out HarEntry? entry))
            {
                entry.Failure = failure;
            }
        }
    }

    private static async Task CaptureBodyAsync(
        CdpPage page,
        PageResponse response,
        HarEntry entry)
    {
        try
        {
            await response.FinishedAsync()
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            NetworkBody body = await page.GetResponseBodyAsync(response).ConfigureAwait(false);
            entry.Body = body.Base64Encoded ? Convert.ToBase64String(body.Data) : body.Text;
            entry.BodyIsBase64 = body.Base64Encoded;
            entry.BodySize = body.Data.Length;
        }
        catch (Exception ex) when (ex is TimeoutException or CdpException or ObjectDisposedException)
        {
            // Keep the metadata-only entry.
        }
    }

    private static void WriteEntry(Utf8JsonWriter writer, HarEntry entry)
    {
        PageRequest request = entry.Request;
        PageResponse? response = entry.Response;

        writer.WriteStartObject();
        writer.WriteString("startedDateTime", FormatTimestamp(request.WallTime));
        writer.WriteNumber("time", ComputeTime(response));
        writer.WritePropertyName("request");
        WriteRequest(writer, request);
        writer.WritePropertyName("response");
        WriteResponse(writer, response, entry);
        writer.WritePropertyName("cache");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WritePropertyName("timings");
        writer.WriteStartObject();
        writer.WriteNumber("send", 0);
        writer.WriteNumber("wait", ComputeTime(response));
        writer.WriteNumber("receive", 0);
        writer.WriteEndObject();

        if (response?.RemoteIPAddress is { Length: > 0 } ip)
        {
            writer.WriteString("serverIPAddress", ip);
        }

        if (response is { ConnectionId: > 0 })
        {
            writer.WriteString(
                "connection",
                response.ConnectionId.ToString(CultureInfo.InvariantCulture));
        }

        if (entry.Failure is { } failure)
        {
            writer.WriteString("_failureText", failure.ErrorText);
        }

        writer.WriteEndObject();
    }

    private static void WriteRequest(Utf8JsonWriter writer, PageRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("method", request.Method);
        writer.WriteString("url", request.Url);
        writer.WriteString("httpVersion", "HTTP/1.1");
        writer.WritePropertyName("headers");
        WriteHeaders(writer, request.Headers);
        writer.WritePropertyName("queryString");
        WriteQueryString(writer, request.Url);
        writer.WritePropertyName("cookies");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber(
            "bodySize",
            request.PostData is null ? 0 : Encoding.UTF8.GetByteCount(request.PostData));
        if (request.PostData is { Length: > 0 } postData)
        {
            writer.WriteString("postData", postData);
        }

        writer.WriteEndObject();
    }

    private static void WriteResponse(
        Utf8JsonWriter writer,
        PageResponse? response,
        HarEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("status", response?.Status ?? 0);
        writer.WriteString("statusText", response?.StatusText ?? string.Empty);
        writer.WriteString("httpVersion", response?.Protocol ?? string.Empty);
        writer.WritePropertyName("headers");
        WriteHeaders(writer, response?.Headers ?? Array.Empty<CdpHeader>());
        writer.WritePropertyName("cookies");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("content");
        writer.WriteStartObject();
        long size = entry.BodySize > 0 ? entry.BodySize : response?.EncodedDataLength ?? 0;
        writer.WriteNumber("size", size);
        writer.WriteString("mimeType", response?.MimeType ?? string.Empty);
        if (entry.Body is not null)
        {
            writer.WriteString("text", entry.Body);
            if (entry.BodyIsBase64)
            {
                writer.WriteString("encoding", "base64");
            }
        }

        writer.WriteEndObject();
        writer.WriteString("redirectURL", string.Empty);
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", size);
        writer.WriteEndObject();
    }

    private static void WriteHeaders(Utf8JsonWriter writer, IReadOnlyList<CdpHeader> headers)
    {
        writer.WriteStartArray();
        foreach (CdpHeader header in headers)
        {
            writer.WriteStartObject();
            writer.WriteString("name", header.Name);
            writer.WriteString("value", header.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteQueryString(Utf8JsonWriter writer, string url)
    {
        writer.WriteStartArray();
        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            int fragmentStart = url.IndexOf('#', queryStart + 1);
            ReadOnlySpan<char> query = fragmentStart >= 0
                ? url.AsSpan(queryStart + 1, fragmentStart - queryStart - 1)
                : url.AsSpan(queryStart + 1);
            foreach (Range range in query.Split('&'))
            {
                ReadOnlySpan<char> pair = query[range];
                int equals = pair.IndexOf('=');
                string name = equals >= 0 ? pair[..equals].ToString() : pair.ToString();
                string value = equals >= 0 ? pair[(equals + 1)..].ToString() : string.Empty;
                writer.WriteStartObject();
                writer.WriteString("name", Uri.UnescapeDataString(name));
                writer.WriteString("value", Uri.UnescapeDataString(value));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }

    private static string FormatTimestamp(double wallTime)
    {
        if (wallTime <= 0)
        {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        long milliseconds = checked((long)(wallTime * 1000));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToString("O", CultureInfo.InvariantCulture);
    }

    private static double ComputeTime(PageResponse? response)
    {
        if (response is null)
        {
            return 0;
        }

        return response.Timing.TryGetValue("receiveHeadersEnd", out double end)
            ? Math.Max(0, end * 1000)
            : 0;
    }

    private sealed class HarEntry
    {
        public HarEntry(PageRequest request) => Request = request;

        public PageRequest Request { get; }

        public PageResponse? Response { get; set; }

        public RequestFailure? Failure { get; set; }

        public string? Body { get; set; }

        public bool BodyIsBase64 { get; set; }

        public long BodySize { get; set; }
    }

    private sealed class Binding
    {
        private readonly HarRecorder _recorder;
        private readonly CdpPage _page;

        public Binding(HarRecorder recorder, CdpPage page)
        {
            _recorder = recorder;
            _page = page;
        }

        public void OnRequest(PageRequest request) => _recorder.OnRequest(request);

        public void OnResponse(PageResponse response) => _recorder.OnResponse(_page, response);

        public void OnFailure(RequestFailure failure) => _recorder.OnFailure(failure);
    }
}
