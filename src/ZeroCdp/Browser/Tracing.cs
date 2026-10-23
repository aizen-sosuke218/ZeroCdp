using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="BrowserContextTracing.StartAsync"/>.</summary>
public sealed class TracingStartOptions
{
    /// <summary>Capture a JPEG of every page when a trace is stopped or a chunk is written.</summary>
    public bool Screenshots { get; init; } = true;

    /// <summary>
    /// Capture a self-contained MHTML snapshot for each page load and when a trace is stopped.
    /// This is a pragmatic DOM snapshot, not Playwright's layout/ARIA snapshot format.
    /// </summary>
    public bool Snapshots { get; init; } = true;

    /// <summary>
    /// Bundle JavaScript, CSS and source-map response bodies in the trace archive. Bodies are read
    /// through <c>Network.getResponseBody</c> and capped at 4 MiB per resource.
    /// </summary>
    public bool Sources { get; init; } = true;

    /// <summary>Optional title shown in trace metadata.</summary>
    public string? Title { get; init; }

    /// <summary>Optional trace name. The stop path still determines the archive name.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Playwright-style trace recording for a browser context. The archive contains
/// <c>trace.trace</c>, <c>trace.network</c>, continuous <c>screencast/</c> frames,
/// <c>snapshots/</c> MHTML documents and <c>sources/</c> resources. Multi-chunk recording exports
/// each chunk independently while keeping the screencast and network observers alive.
/// </summary>
public sealed class BrowserContextTracing
{
    private readonly BrowserContext _context;
    private readonly Lock _gate = new();
    private TraceSession? _state;

    internal BrowserContextTracing(BrowserContext context) => _context = context;

    /// <summary>Starts a trace. Only one trace can be active per context.</summary>
    public async ValueTask StartAsync(
        TracingStartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new TracingStartOptions();

        TraceSession state = new(_context, options);
        lock (_gate)
        {
            if (_state is not null)
            {
                throw new InvalidOperationException("Tracing has already been started.");
            }

            _state = state;
        }

        Attach(state);
        try
        {
            await state.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Detach(state);
            lock (_gate)
            {
                if (ReferenceEquals(_state, state))
                {
                    _state = null;
                }
            }

            await state.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts a trace if none is active. A trace already has a current chunk, so this is a no-op
    /// while recording. After <see cref="StopChunkAsync"/> the next chunk starts automatically.
    /// </summary>
    public async ValueTask StartChunkAsync(
        TracingStartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TraceSession? state;
        lock (_gate)
        {
            state = _state;
        }

        if (state is null)
        {
            await StartAsync(options, cancellationToken).ConfigureAwait(false);
            return;
        }

        await state.EnsureChunkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops tracing and writes the final chunk as a Playwright-style trace archive.</summary>
    public async ValueTask StopAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        TraceSession? state;
        lock (_gate)
        {
            state = _state;
            _state = null;
        }

        if (state is null)
        {
            throw new InvalidOperationException("Tracing has not been started.");
        }

        Detach(state);
        await state.StopAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the current chunk without stopping recording. Frames, snapshots and source resources
    /// are flushed into the archive, then a fresh chunk is started for subsequent events.
    /// </summary>
    public async ValueTask StopChunkAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        TraceSession? state;
        lock (_gate)
        {
            state = _state;
        }

        if (state is null)
        {
            throw new InvalidOperationException("Tracing has not been started.");
        }

        await state.StopChunkAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private void Attach(TraceSession state)
    {
        _context.PageAdded += state.OnPageAdded;
        _context.PageClosed += state.OnPageClosed;
        _context.Request += state.OnRequest;
        _context.Response += state.OnResponse;
        _context.RequestFailed += state.OnRequestFailed;
    }

    private void Detach(TraceSession state)
    {
        _context.PageAdded -= state.OnPageAdded;
        _context.PageClosed -= state.OnPageClosed;
        _context.Request -= state.OnRequest;
        _context.Response -= state.OnResponse;
        _context.RequestFailed -= state.OnRequestFailed;
    }

    private sealed class TraceSession : IAsyncDisposable
    {
        private const double FrameIntervalSeconds = 0.2;
        private const int MaxSourceBytes = 4 * 1024 * 1024;

        private readonly BrowserContext _context;
        private readonly TracingStartOptions _options;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _wallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private readonly Lock _gate = new();
        private readonly string _tempRoot;
        private readonly Channel<FrameWork> _frames;
        private readonly Task _frameWriter;
        private readonly Dictionary<string, PageEntry> _pages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PageRequest> _requests = new(StringComparer.Ordinal);
        private readonly List<string> _network = new();
        private readonly List<Task> _pending = new();
        private TraceChunk? _chunk;
        private Exception? _frameError;
        private bool _stopping;

        public TraceSession(BrowserContext context, TracingStartOptions options)
        {
            _context = context;
            _options = options;
            _tempRoot = Path.Combine(
                Path.GetTempPath(),
                "zerocdp-trace-" + Guid.NewGuid().ToString("N"));
            _frames = Channel.CreateBounded<FrameWork>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
            _frameWriter = Task.Run(WriteFramesAsync);
        }

        private ViewportSize Viewport => _context.Options?.Viewport ?? ViewportSize.Default;

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_tempRoot);
            lock (_gate)
            {
                _chunk = CreateChunkLocked();
            }

            foreach (CdpPage page in _context.Pages)
            {
                OnPageAdded(page);
            }

            await EnsureChunkAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask EnsureChunkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_chunk is null && !_stopping)
                {
                    _chunk = CreateChunkLocked();
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void OnPageAdded(CdpPage page)
        {
            if (page.IsClosed)
            {
                return;
            }

            string pageId = PageId(page);
            PageEntry? entry;
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                if (!_pages.TryGetValue(pageId, out entry))
                {
                    entry = new PageEntry(page, pageId);
                    _pages[pageId] = entry;
                    AddPageEventLocked(pageId);
                }
            }

            if (entry is null || entry.Started || entry.StartScheduled)
            {
                return;
            }

            if (page.IsEnabled)
            {
                ScheduleStart(entry);
                return;
            }

            entry.EnabledHandler = () => ScheduleStart(entry);
            page.Enabled += entry.EnabledHandler;

            // EnableAsync may have completed between the IsEnabled check and the subscription.
            if (page.IsEnabled)
            {
                page.Enabled -= entry.EnabledHandler;
                entry.EnabledHandler = null;
                ScheduleStart(entry);
            }
        }

        public void OnPageClosed(CdpPage page)
        {
            string pageId = PageId(page);
            PageEntry? entry = null;
            lock (_gate)
            {
                if (_pages.Remove(pageId, out entry))
                {
                    _chunk?.Trace.Add(
                        EventLine("Page", "close", "{}", pageId));
                }
            }

            if (entry is not null)
            {
                Track(DetachPageAsync(entry));
            }
        }

        private void ScheduleStart(PageEntry entry)
        {
            lock (_gate)
            {
                if (_stopping || entry.Started || entry.StartScheduled)
                {
                    return;
                }

                entry.StartScheduled = true;
            }

            Track(StartPageAsync(entry));
        }

        private async Task StartPageAsync(PageEntry entry)
        {
            ScreencastHub.Subscription? subscription = null;
            try
            {
                CdpPage page = entry.Page;
                if (page.IsClosed)
                {
                    return;
                }

                if (entry.EnabledHandler is not null)
                {
                    page.Enabled -= entry.EnabledHandler;
                    entry.EnabledHandler = null;
                }

                if (_options.Snapshots)
                {
                    entry.LoadHandler = () => OnPageLoad(entry);
                    page.Load += entry.LoadHandler;
                }

                if (_options.Sources)
                {
                    entry.ResponseHandler = response => OnPageResponse(entry, response);
                    page.Response += entry.ResponseHandler;
                }

                if (_options.Screenshots)
                {
                    ViewportSize viewport = Viewport;
                    subscription = await page.Screencast
                        .SubscribeAsync(
                            viewport.Width,
                            viewport.Height,
                            data => OnFrame(entry, data),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (_stopping || page.IsClosed)
                    {
                        // The page closed while the screencast was starting.
                    }
                    else
                    {
                        entry.Screencast = subscription;
                        entry.Started = true;
                        subscription = null;
                    }
                }
            }
            catch (Exception ex) when (ex is CdpException
                or ObjectDisposedException
                or InvalidOperationException)
            {
                // A page can close while tracing is attaching to it.
            }
            finally
            {
                if (subscription is not null)
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private void OnPageLoad(PageEntry entry)
        {
            if (!_options.Snapshots || entry.Page.IsClosed)
            {
                return;
            }

            TraceChunk? chunk;
            double now = _clock.Elapsed.TotalSeconds;
            lock (_gate)
            {
                if (_stopping || _chunk is null || now - entry.LastSnapshot < 0.5)
                {
                    return;
                }

                entry.LastSnapshot = now;
                chunk = _chunk;
            }

            Track(CaptureSnapshotAsync(entry, chunk, "load"));
        }

        private void OnPageResponse(PageEntry entry, PageResponse response)
        {
            if (!_options.Sources || !ShouldCaptureSource(response))
            {
                return;
            }

            TraceChunk? chunk;
            lock (_gate)
            {
                chunk = _chunk;
            }

            if (chunk is not null)
            {
                Track(CaptureSourceAsync(entry, response, chunk));
            }
        }

        private void OnFrame(PageEntry entry, ReadOnlyMemory<byte> data)
        {
            double now = _clock.Elapsed.TotalSeconds;
            lock (_gate)
            {
                if (_stopping || _chunk is null || data.Length == 0)
                {
                    return;
                }

                if (entry.LastFrame >= 0 && now - entry.LastFrame < FrameIntervalSeconds)
                {
                    return;
                }

                entry.LastFrame = now;
                TraceChunk chunk = _chunk;
                int sequence = ++chunk.FrameSequence;
                string zipName = "screencast/" + SafeName(entry.PageId) + "-"
                    + sequence.ToString(CultureInfo.InvariantCulture) + ".jpeg";

                // Enqueue while holding the chunk gate so StopChunkAsync cannot archive the chunk
                // between assigning a frame to it and writing the frame to the channel.
                _frames.Writer.TryWrite(
                    new FrameWork(chunk, entry.PageId, zipName, data.ToArray(), now));
            }
        }

        public void OnRequest(PageRequest request)
        {
            lock (_gate)
            {
                _requests[request.RequestId] = request;
            }
        }

        public void OnResponse(PageResponse response)
        {
            PageRequest? request;
            lock (_gate)
            {
                if (!_requests.Remove(response.RequestId, out request))
                {
                    return;
                }
            }

            string? line = TryNetworkLine(request, response, failure: null);
            if (line is not null)
            {
                lock (_gate)
                {
                    _network.Add(line);
                }
            }
        }

        public void OnRequestFailed(RequestFailure failure)
        {
            PageRequest? request;
            lock (_gate)
            {
                if (!_requests.Remove(failure.RequestId, out request))
                {
                    return;
                }
            }

            string? line = TryNetworkLine(request, response: null, failure);
            if (line is not null)
            {
                lock (_gate)
                {
                    _network.Add(line);
                }
            }
        }

        public async ValueTask StopChunkAsync(
            string path,
            CancellationToken cancellationToken)
        {
            TraceChunk? chunk;
            lock (_gate)
            {
                chunk = _chunk;
                _chunk = null;
                if (!_stopping)
                {
                    // Keep recording immediately while the old chunk is snapshotted and zipped.
                    _chunk = CreateChunkLocked();
                }
            }

            if (chunk is null)
            {
                return;
            }

            // Late load/response events now target the new chunk; final snapshots and screenshots
            // target the detached chunk explicitly.
            await CaptureFinalPageStateAsync(chunk, cancellationToken).ConfigureAwait(false);
            await WaitPendingAsync().ConfigureAwait(false);
            await FlushFramesAsync(cancellationToken).ConfigureAwait(false);
            await WriteArchiveAsync(chunk, path, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask StopAsync(
            string path,
            CancellationToken cancellationToken)
        {
            PageEntry[] pages;
            TraceChunk? chunk;
            lock (_gate)
            {
                _stopping = true;
                pages = _pages.Values.ToArray();
                chunk = _chunk;
                _chunk = null;
            }

            foreach (PageEntry entry in pages)
            {
                await DetachPageAsync(entry).ConfigureAwait(false);
            }

            await WaitPendingAsync().ConfigureAwait(false);

            if (chunk is not null)
            {
                await CaptureFinalPageStateAsync(chunk, cancellationToken).ConfigureAwait(false);
                await WaitPendingAsync().ConfigureAwait(false);
                await FlushFramesAsync(cancellationToken).ConfigureAwait(false);
                await WriteArchiveAsync(chunk, path, cancellationToken).ConfigureAwait(false);
            }

            _frames.Writer.TryComplete();
            try
            {
                await _frameWriter.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            DeleteTempRoot();
            if (_frameError is not null)
            {
                throw new InvalidOperationException(
                    "A trace frame could not be written.",
                    _frameError);
            }
        }

        private async Task CaptureFinalPageStateAsync(
            TraceChunk chunk,
            CancellationToken cancellationToken)
        {
            PageEntry[] pages;
            lock (_gate)
            {
                pages = _pages.Values.ToArray();
            }

            foreach (PageEntry entry in pages)
            {
                if (entry.Page.IsClosed)
                {
                    continue;
                }

                if (_options.Snapshots)
                {
                    await CaptureSnapshotAsync(entry, chunk, "stop", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (_options.Screenshots)
                {
                    await CaptureScreenshotAsync(entry, chunk, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task CaptureScreenshotAsync(
            PageEntry entry,
            TraceChunk chunk,
            CancellationToken cancellationToken)
        {
            try
            {
                CdpPage page = entry.Page;
                string url;
                try
                {
                    url = await page.UrlAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (CdpException)
                {
                    url = string.Empty;
                }

                lock (_gate)
                {
                    AddPageEventLocked(chunk, entry.PageId);
                    chunk.Trace.Add(
                        EventLine(
                            "Frame",
                            "navigated",
                            $"{{\"url\":{JsText.Quote(url)}}}",
                            entry.PageId));
                }

                byte[] image = await page
                    .ScreenshotBytesAsync(
                        new ScreenshotOptions
                        {
                            Format = ScreenshotFormat.Jpeg,
                            Quality = 80,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (image.Length == 0)
                {
                    return;
                }

                int sequence;
                lock (_gate)
                {
                    sequence = ++chunk.FrameSequence;
                }

                string zipName = "screencast/" + SafeName(entry.PageId) + "-"
                    + sequence.ToString(CultureInfo.InvariantCulture) + ".jpeg";
                string file = Path.Combine(chunk.DirectoryPath, "screenshot-" + sequence + ".jpeg");
                await File.WriteAllBytesAsync(file, image, cancellationToken).ConfigureAwait(false);
                ViewportSize viewport = Viewport;
                lock (_gate)
                {
                    chunk.Resources[zipName] = file;
                    chunk.Trace.Add(
                        ScreencastFrameLine(
                            entry.PageId,
                            zipName,
                            viewport.Width,
                            viewport.Height,
                            _clock.Elapsed.TotalSeconds));
                }
            }
            catch (Exception ex) when (ex is CdpException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException)
            {
                // A page can close between enumeration and capture.
            }
        }

        private async Task CaptureSnapshotAsync(
            PageEntry entry,
            TraceChunk chunk,
            string reason,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Snapshots || entry.Page.IsClosed)
            {
                return;
            }

            if (!await entry.SnapshotGate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            try
            {
                string mhtml = await entry.Page
                    .CaptureMhtmlAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (mhtml.Length == 0)
                {
                    return;
                }

                byte[] data = Encoding.UTF8.GetBytes(mhtml);
                int sequence;
                lock (_gate)
                {
                    sequence = ++chunk.SnapshotSequence;
                }

                string zipName = "snapshots/" + SafeName(entry.PageId) + "-"
                    + sequence.ToString(CultureInfo.InvariantCulture) + ".mhtml";
                string file = Path.Combine(chunk.DirectoryPath, "snapshot-" + sequence + ".mhtml");
                await File.WriteAllBytesAsync(file, data, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    chunk.Resources[zipName] = file;
                    chunk.Trace.Add(
                        EventLine(
                            "Frame",
                            "snapshot",
                            "{\"file\":" + JsText.Quote(zipName)
                                + ",\"reason\":" + JsText.Quote(reason) + "}",
                            entry.PageId));
                }
            }
            catch (Exception ex) when (ex is CdpException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException)
            {
                // Snapshot capture is best-effort when a navigation or close races it.
            }
            finally
            {
                entry.SnapshotGate.Release();
            }
        }

        private async Task CaptureSourceAsync(
            PageEntry entry,
            PageResponse response,
            TraceChunk chunk)
        {
            try
            {
                if (response.EncodedDataLength > MaxSourceBytes)
                {
                    return;
                }

                await response.FinishedAsync()
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                NetworkBody body = await entry.Page
                    .GetResponseBodyAsync(response, CancellationToken.None)
                    .ConfigureAwait(false);
                if (body.Data.Length is 0 or > MaxSourceBytes)
                {
                    return;
                }

                if (!await AddSourceResourceAsync(
                        chunk,
                        response.Url,
                        body.Data,
                        SourceExtension(response),
                        isMap: false)
                        .ConfigureAwait(false))
                {
                    return;
                }

                string? mapUrl = FindSourceMapUrl(body.Text);
                if (!string.IsNullOrEmpty(mapUrl))
                {
                    await CaptureSourceMapAsync(entry, response.Url, chunk, mapUrl)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is CdpException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException
                or TimeoutException)
            {
                // Source capture is optional and must never interfere with page execution.
            }
        }

        private async Task CaptureSourceMapAsync(
            PageEntry entry,
            string sourceUrl,
            TraceChunk chunk,
            string sourceMapUrl)
        {
            try
            {
                byte[]? data = null;
                string resolved = sourceMapUrl;
                if (sourceMapUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    data = DecodeDataUri(sourceMapUrl);
                }
                else
                {
                    resolved = ResolveUrl(sourceUrl, sourceMapUrl);
                    if (resolved.Length == 0)
                    {
                        return;
                    }

                    string expression = "fetch(" + JsText.Quote(resolved) + ")"
                        + ".then(r => r.ok ? r.text() : '')"
                        + ".catch(() => '')";
                    using CancellationTokenSource timeout =
                        new(TimeSpan.FromSeconds(2));
                    EvaluationResult result = await entry.Page
                        .EvaluateAsync(expression, timeout.Token)
                        .ConfigureAwait(false);
                    if (result.Threw || string.IsNullOrEmpty(result.Value.StringValue))
                    {
                        return;
                    }

                    data = Encoding.UTF8.GetBytes(result.Value.StringValue);
                }

                if (data is null or { Length: 0 } || data.Length > MaxSourceBytes)
                {
                    return;
                }

                await AddSourceResourceAsync(chunk, resolved, data, ".map", isMap: true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException
                or FormatException
                or OperationCanceledException
                or TimeoutException)
            {
                // A cross-origin or expired source map must not break tracing.
            }
        }

        private async Task<bool> AddSourceResourceAsync(
            TraceChunk chunk,
            string url,
            byte[] data,
            string extension,
            bool isMap)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data))
                .ToLowerInvariant();
            string zipName = "sources/" + hash + extension;
            lock (_gate)
            {
                if (chunk.Resources.ContainsKey(zipName))
                {
                    return false;
                }
            }

            string file = Path.Combine(
                chunk.DirectoryPath,
                hash + "-" + Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(file, data).ConfigureAwait(false);
            lock (_gate)
            {
                if (chunk.Resources.ContainsKey(zipName))
                {
                    return false;
                }

                chunk.Resources[zipName] = file;
                chunk.Trace.Add(
                    EventLine(
                        "BrowserContext",
                        "source",
                        "{\"url\":" + JsText.Quote(url)
                            + ",\"file\":" + JsText.Quote(zipName)
                            + ",\"sha256\":" + JsText.Quote(hash)
                            + ",\"sourceMap\":" + (isMap ? "true" : "false") + "}",
                        null));
            }

            return true;
        }

        private static string? FindSourceMapUrl(string text)
        {
            const string marker = "sourceMappingURL=";
            int index = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            int start = index + marker.Length;
            int end = start;
            while (end < text.Length)
            {
                char c = text[end];
                if (char.IsWhiteSpace(c) || c is '"' or '\'' or '*' or '/' or '\\')
                {
                    break;
                }

                end++;
            }

            return end > start ? text[start..end] : null;
        }

        private static string ResolveUrl(string baseUrl, string relative)
        {
            if (Uri.TryCreate(relative, UriKind.Absolute, out Uri? absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
                && Uri.TryCreate(baseUri, relative, out Uri? resolved))
            {
                return resolved.ToString();
            }

            return string.Empty;
        }

        private static byte[]? DecodeDataUri(string dataUri)
        {
            int comma = dataUri.IndexOf(',');
            if (comma < 0 || comma == dataUri.Length - 1)
            {
                return null;
            }

            string metadata = dataUri[..comma];
            string payload = dataUri[(comma + 1)..];
            if (metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.FromBase64String(payload);
            }

            return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

        private static async Task DetachPageAsync(PageEntry entry)
        {
            CdpPage page = entry.Page;
            if (entry.EnabledHandler is not null)
            {
                page.Enabled -= entry.EnabledHandler;
                entry.EnabledHandler = null;
            }

            if (entry.LoadHandler is not null)
            {
                page.Load -= entry.LoadHandler;
                entry.LoadHandler = null;
            }

            if (entry.ResponseHandler is not null)
            {
                page.Response -= entry.ResponseHandler;
                entry.ResponseHandler = null;
            }

            ScreencastHub.Subscription? subscription = entry.Screencast;
            entry.Screencast = null;
            entry.Started = false;
            if (subscription is not null)
            {
                try
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CdpException
                    or ObjectDisposedException
                    or IOException
                    or InvalidOperationException)
                {
                }
            }
        }

        private async Task FlushFramesAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_frames.Writer.TryWrite(
                    new FrameWork(null, null, null, null, 0, completion)))
            {
                return;
            }

            await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task WriteFramesAsync()
        {
            try
            {
                await foreach (FrameWork work in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (work.Completion is not null)
                    {
                        work.Completion.TrySetResult();
                        continue;
                    }

                    if (work.Chunk is null || work.ZipName is null || work.Data is null)
                    {
                        continue;
                    }

                    try
                    {
                        string file = Path.Combine(
                            work.Chunk.DirectoryPath,
                            "frame-" + Guid.NewGuid().ToString("N") + ".jpeg");
                        await File.WriteAllBytesAsync(file, work.Data).ConfigureAwait(false);
                        lock (_gate)
                        {
                            work.Chunk.Resources[work.ZipName] = file;
                            work.Chunk.Trace.Add(
                                ScreencastFrameLine(
                                    work.PageId!,
                                    work.ZipName,
                                    Viewport.Width,
                                    Viewport.Height,
                                    work.Time));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        lock (_gate)
                        {
                            _frameError ??= ex;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                lock (_gate)
                {
                    _frameError ??= ex;
                }
            }
        }

        private async Task WaitPendingAsync()
        {
            while (true)
            {
                Task[] tasks;
                lock (_gate)
                {
                    tasks = _pending.ToArray();
                }

                if (tasks.Length == 0)
                {
                    return;
                }

                try
                {
                    await Task.WhenAll(tasks)
                        .WaitAsync(TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CdpException
                    or ObjectDisposedException
                    or IOException
                    or InvalidOperationException
                    or TimeoutException)
                {
                    return;
                }
            }
        }

        private void Track(Task task)
        {
            lock (_gate)
            {
                _pending.Add(task);
            }

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_gate)
                    {
                        _pending.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async ValueTask WriteArchiveAsync(
            TraceChunk chunk,
            string path,
            CancellationToken cancellationToken)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string[] trace;
            string[] network;
            KeyValuePair<string, string>[] resources;
            lock (_gate)
            {
                trace = chunk.Trace.ToArray();
                network = _network.ToArray();
                resources = chunk.Resources.ToArray();
            }

            await using FileStream stream = new(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);
            using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true);

            await WriteTextAsync(
                    archive,
                    "trace.trace",
                    string.Join('\n', trace) + "\n",
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    archive,
                    "trace.network",
                    network.Length == 0 ? string.Empty : string.Join('\n', network) + "\n",
                    cancellationToken)
                .ConfigureAwait(false);

            foreach ((string name, string file) in resources)
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                await using Stream entryStream = entry.Open();
                await using FileStream source = new(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                Directory.Delete(chunk.DirectoryPath, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static async ValueTask WriteTextAsync(
            ZipArchive archive,
            string name,
            string text,
            CancellationToken cancellationToken)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            await using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        private string ContextOptionsLine(TracingStartOptions options)
        {
            StringBuilder builder = new(256);
            builder.Append("{\"version\":8,\"type\":\"context-options\",\"origin\":\"library\"");
            builder.Append(",\"browserName\":\"chromium\"");
            builder.Append(",\"platform\":").Append(JsText.Quote(Platform()));
            builder.Append(",\"playwrightVersion\":\"0.1.0-zero-cdp\"");
            builder.Append(",\"wallTime\":")
                .Append(_wallTime.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"monotonicTime\":0");
            if (!string.IsNullOrEmpty(options.Title))
            {
                builder.Append(",\"title\":").Append(JsText.Quote(options.Title));
            }

            builder.Append(",\"options\":{},\"sdkLanguage\":\"csharp\"}");
            return builder.ToString();
        }

        private string EventLine(
            string className,
            string method,
            string parameters,
            string? pageId)
        {
            StringBuilder builder = new(128);
            builder.Append("{\"type\":\"event\",\"time\":")
                .Append(_clock.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(",\"class\":").Append(JsText.Quote(className));
            builder.Append(",\"method\":").Append(JsText.Quote(method));
            builder.Append(",\"params\":").Append(parameters);
            if (pageId is not null)
            {
                builder.Append(",\"pageId\":").Append(JsText.Quote(pageId));
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string ScreencastFrameLine(
            string pageId,
            string file,
            int width,
            int height,
            double timestamp)
        {
            StringBuilder builder = new(256);
            builder.Append("{\"type\":\"screencast-frame\",\"pageId\":")
                .Append(JsText.Quote(pageId));
            builder.Append(",\"file\":").Append(JsText.Quote(file));
            builder.Append(",\"width\":").Append(width.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"height\":").Append(height.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"timestamp\":")
                .Append(timestamp.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append('}');
            return builder.ToString();
        }

        private static string? TryNetworkLine(
            PageRequest request,
            PageResponse? response,
            RequestFailure? failure)
        {
            try
            {
                return NetworkLine(request, response, failure);
            }
            catch (Exception ex) when (ex is ArgumentException
                or FormatException
                or JsonException
                or InvalidOperationException
                or OverflowException)
            {
                // A malformed URL or header must never break the CDP reader loop.
                return null;
            }
        }

        private static string NetworkLine(
            PageRequest request,
            PageResponse? response,
            RequestFailure? failure)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "resource-snapshot");
                writer.WritePropertyName("snapshot");
                WriteHarEntry(writer, request, response, failure);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteHarEntry(
            Utf8JsonWriter writer,
            PageRequest request,
            PageResponse? response,
            RequestFailure? failure)
        {
            writer.WriteStartObject();
            writer.WriteString("startedDateTime", FormatTimestamp(request.WallTime));
            writer.WriteNumber("time", ComputeTime(response));
            writer.WritePropertyName("request");
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
            if (!string.IsNullOrEmpty(request.PostData))
            {
                writer.WriteString("postData", request.PostData);
            }

            writer.WriteEndObject();

            writer.WritePropertyName("response");
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
            writer.WriteNumber("size", response?.EncodedDataLength ?? 0);
            writer.WriteString("mimeType", response?.MimeType ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteString("redirectURL", string.Empty);
            writer.WriteNumber("headersSize", -1);
            writer.WriteNumber("bodySize", response?.EncodedDataLength ?? 0);
            writer.WriteEndObject();

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

            if (failure is not null)
            {
                writer.WriteString("_failureText", failure.ErrorText);
            }

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
                    string value = equals >= 0
                        ? pair[(equals + 1)..].ToString()
                        : string.Empty;
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

        private static bool ShouldCaptureSource(PageResponse response)
        {
            if (response.Status is < 200 or >= 300)
            {
                return false;
            }

            string resourceType = response.Request?.ResourceType ?? string.Empty;
            if (resourceType.Equals("Script", StringComparison.OrdinalIgnoreCase)
                || resourceType.Equals("Stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string url = response.Url;
            string mime = response.MimeType;
            return url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
                || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || mime.Contains("text/css", StringComparison.OrdinalIgnoreCase)
                || mime.Contains("source-map", StringComparison.OrdinalIgnoreCase);
        }

        private static string SourceExtension(PageResponse response)
        {
            string url = response.Url;
            if (url.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            {
                return ".map";
            }

            if (url.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                || response.MimeType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
            {
                return ".css";
            }

            if (url.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                return ".mjs";
            }

            return ".js";
        }

        private static string SafeName(string value)
        {
            StringBuilder builder = new(value.Length);
            foreach (char c in value)
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
            }

            return builder.ToString();
        }

        private void AddPageEventLocked(string pageId)
        {
            if (_chunk is not null)
            {
                AddPageEventLocked(_chunk, pageId);
            }
        }

        private void AddPageEventLocked(TraceChunk chunk, string pageId)
        {
            if (!chunk.Pages.Add(pageId))
            {
                return;
            }

            chunk.Trace.Add(
                EventLine(
                    "BrowserContext",
                    "page",
                    $"{{\"page\":{{\"guid\":{JsText.Quote(pageId)}}}}}",
                    pageId));
        }

        private TraceChunk CreateChunkLocked()
        {
            TraceChunk chunk = new(
                Path.Combine(_tempRoot, "chunk-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(chunk.DirectoryPath);
            chunk.Trace.Add(ContextOptionsLine(_options));
            foreach (PageEntry entry in _pages.Values)
            {
                if (chunk.Pages.Add(entry.PageId))
                {
                    chunk.Trace.Add(
                        EventLine(
                            "BrowserContext",
                            "page",
                            $"{{\"page\":{{\"guid\":{JsText.Quote(entry.PageId)}}}}}",
                            entry.PageId));
                }
            }

            return chunk;
        }

        private void DeleteTempRoot()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            PageEntry[] pages;
            lock (_gate)
            {
                _stopping = true;
                pages = _pages.Values.ToArray();
            }

            foreach (PageEntry entry in pages)
            {
                await DetachPageAsync(entry).ConfigureAwait(false);
            }

            _frames.Writer.TryComplete();
            try
            {
                await _frameWriter.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            DeleteTempRoot();
        }

        private static string PageId(CdpPage page) => "page@" + page.TargetId;

        private static string Platform()
        {
            if (OperatingSystem.IsWindows())
            {
                return "win32";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "darwin";
            }

            return "linux";
        }

        private sealed class PageEntry
        {
            public PageEntry(CdpPage page, string pageId)
            {
                Page = page;
                PageId = pageId;
            }

            public CdpPage Page { get; }

            public string PageId { get; }

            public Action? EnabledHandler { get; set; }

            public Action? LoadHandler { get; set; }

            public Action<PageResponse>? ResponseHandler { get; set; }

            public ScreencastHub.Subscription? Screencast { get; set; }

            public SemaphoreSlim SnapshotGate { get; } = new(1, 1);

            public bool Started { get; set; }

            public bool StartScheduled { get; set; }

            public double LastFrame { get; set; } = -1;

            public double LastSnapshot { get; set; } = -1;
        }

        private sealed class TraceChunk
        {
            public TraceChunk(string directoryPath) => DirectoryPath = directoryPath;

            public string DirectoryPath { get; }

            public List<string> Trace { get; } = new();

            public Dictionary<string, string> Resources { get; } = new(StringComparer.Ordinal);

            public HashSet<string> Pages { get; } = new(StringComparer.Ordinal);

            public int FrameSequence { get; set; }

            public int SnapshotSequence { get; set; }
        }

        private sealed class FrameWork
        {
            public FrameWork(
                TraceChunk? chunk,
                string? pageId,
                string? zipName,
                byte[]? data,
                double time,
                TaskCompletionSource? completion = null)
            {
                Chunk = chunk;
                PageId = pageId;
                ZipName = zipName;
                Data = data;
                Time = time;
                Completion = completion;
            }

            public TraceChunk? Chunk { get; }

            public string? PageId { get; }

            public string? ZipName { get; }

            public byte[]? Data { get; }

            public double Time { get; }

            public TaskCompletionSource? Completion { get; }
        }
    }
}
