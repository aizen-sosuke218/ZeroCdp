using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ZeroCdp.Browser;

/// <summary>Metadata stored at the beginning of a Playwright-style trace archive.</summary>
public sealed class TraceContextOptions
{
    public int Version { get; init; }

    public string Origin { get; init; } = string.Empty;

    public string BrowserName { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string PlaywrightVersion { get; init; } = string.Empty;

    public string? Title { get; init; }

    public long WallTime { get; init; }

    public double MonotonicTime { get; init; }

    public string RawJson { get; init; } = string.Empty;
}

/// <summary>One entry from <c>trace.trace</c>: an action event, before/after hook or a marker.</summary>
public sealed class TraceAction
{
    /// <summary>Protocol type, normally <c>event</c>, <c>before</c> or <c>after</c>.</summary>
    public string Type { get; init; } = string.Empty;

    public double Time { get; init; }

    public string Class { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string? PageId { get; init; }

    /// <summary>Parameters as a detached JSON element. Missing parameters are an empty object.</summary>
    public JsonElement Params { get; init; }

    public string RawJson { get; init; } = string.Empty;

    /// <summary>Human-readable action name, for example <c>Frame.navigated</c>.</summary>
    public string Name => string.IsNullOrEmpty(Class) ? Method : Class + "." + Method;
}

/// <summary>A JPEG frame from the trace's <c>screencast/</c> directory.</summary>
public sealed class TraceScreencastFrame
{
    public string PageId { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public double Timestamp { get; init; }

    public string RawJson { get; init; } = string.Empty;
}

/// <summary>A JavaScript or CSS resource captured by the trace recorder.</summary>
public sealed class TraceSource
{
    public string Url { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public bool IsSourceMap { get; init; }

    public string RawJson { get; init; } = string.Empty;
}

/// <summary>One HAR-shaped network entry from <c>trace.network</c>.</summary>
public sealed class TraceNetworkEntry
{
    public string Method { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public int Status { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string StartedDateTime { get; init; } = string.Empty;

    public double Time { get; init; }

    public long BodySize { get; init; }

    public long ResponseBodySize { get; init; }

    public string? FailureText { get; init; }

    public JsonElement Snapshot { get; init; }

    public string RawJson { get; init; } = string.Empty;
}

/// <summary>
/// Read-only access to a Playwright-style trace archive written by
/// <see cref="BrowserContextTracing"/>. The archive may be a ZIP file or an extracted directory.
/// It parses <c>trace.trace</c>, <c>trace.network</c>, screencast frames, snapshots and source
/// resources into typed records, and can generate a self-contained HTML viewer.
/// </summary>
/// <remarks>
/// This is a library-level trace reader and a lightweight viewer, not Playwright's React trace
/// viewer. Unknown trace records are preserved in the entry list and reported through
/// <see cref="Warnings"/> instead of aborting the whole archive.
/// </remarks>
public sealed class TraceArchive : IDisposable
{
    private readonly FileStream? _stream;
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _zipEntries;
    private readonly Dictionary<string, string> _files;
    private readonly Lock _gate = new();
    private bool _disposed;

    private TraceArchive(
        string sourcePath,
        bool isDirectory,
        FileStream? stream,
        ZipArchive? zip,
        Dictionary<string, ZipArchiveEntry> zipEntries,
        Dictionary<string, string> files)
    {
        SourcePath = sourcePath;
        IsDirectory = isDirectory;
        _stream = stream;
        _zip = zip;
        _zipEntries = zipEntries;
        _files = files;
    }

    /// <summary>Full path supplied when the archive was opened.</summary>
    public string SourcePath { get; }

    /// <summary>True when <see cref="SourcePath"/> is an extracted trace directory.</summary>
    public bool IsDirectory { get; }

    public TraceContextOptions? Context { get; private init; }

    public IReadOnlyList<TraceAction> Actions { get; private init; } = Array.Empty<TraceAction>();

    public IReadOnlyList<TraceScreencastFrame> ScreencastFrames { get; private init; }
        = Array.Empty<TraceScreencastFrame>();

    public IReadOnlyList<TraceSource> Sources { get; private init; } = Array.Empty<TraceSource>();

    public IReadOnlyList<TraceNetworkEntry> Network { get; private init; }
        = Array.Empty<TraceNetworkEntry>();

    public IReadOnlyList<string> EntryNames { get; private init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>Optional title from the trace's <c>context-options</c> record.</summary>
    public string? Title => Context?.Title;

    /// <summary>Opens a trace ZIP or an extracted trace directory.</summary>
    public static TraceArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            return OpenDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Trace archive was not found.", fullPath);
        }

        FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: false);
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                entries[NormalizeName(entry.FullName)] = entry;
            }

            return Parse(fullPath, isDirectory: false, stream, zip, entries, null);
        }
        catch
        {
            zip?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Asynchronous counterpart of <see cref="Open"/> for callers that prefer async flow.</summary>
    public static ValueTask<TraceArchive> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Open(path));
    }

    private static TraceArchive OpenDirectory(string directory)
    {
        string traceFile = Path.Combine(directory, "trace.trace");
        if (!File.Exists(traceFile))
        {
            throw new InvalidDataException($"Trace directory does not contain trace.trace: {directory}");
        }

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            files[NormalizeName(relative)] = file;
        }

        return Parse(directory, isDirectory: true, null, null, null, files);
    }

    private static TraceArchive Parse(
        string sourcePath,
        bool isDirectory,
        FileStream? stream,
        ZipArchive? zip,
        Dictionary<string, ZipArchiveEntry>? zipEntries,
        Dictionary<string, string>? files)
    {
        List<string> warnings = new();
        string traceText;
        byte[] traceBytes;
        if (isDirectory)
        {
            traceBytes = File.ReadAllBytes(Path.Combine(sourcePath, "trace.trace"));
        }
        else
        {
            ZipArchiveEntry entry = zipEntries!["trace.trace"];
            using Stream entryStream = entry.Open();
            using MemoryStream buffer = new();
            entryStream.CopyTo(buffer);
            traceBytes = buffer.ToArray();
        }

        traceText = Encoding.UTF8.GetString(traceBytes);
        List<TraceAction> actions = new();
        List<TraceScreencastFrame> frames = new();
        List<TraceSource> sources = new();
        TraceContextOptions? context = null;

        foreach (string line in EnumerateLines(traceText))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string type = GetString(root, "type");
                switch (type)
                {
                    case "context-options":
                        context = new TraceContextOptions
                        {
                            Version = GetInt32(root, "version"),
                            Origin = GetString(root, "origin"),
                            BrowserName = GetString(root, "browserName"),
                            Platform = GetString(root, "platform"),
                            PlaywrightVersion = GetString(root, "playwrightVersion"),
                            Title = GetNullableString(root, "title"),
                            WallTime = GetInt64(root, "wallTime"),
                            MonotonicTime = GetDouble(root, "monotonicTime"),
                            RawJson = line,
                        };
                        break;

                    case "event":
                    case "before":
                    case "after":
                    {
                        TraceAction action = new()
                        {
                            Type = type,
                            Time = GetDouble(root, "time"),
                            Class = GetString(root, "class"),
                            Method = GetString(root, "method"),
                            PageId = GetNullableString(root, "pageId"),
                            Params = GetDetachedParams(root),
                            RawJson = line,
                        };
                        actions.Add(action);
                        if (action.Class == "BrowserContext" && action.Method == "source")
                        {
                            sources.Add(new TraceSource
                            {
                                Url = GetString(action.Params, "url"),
                                File = GetString(action.Params, "file"),
                                Sha256 = GetString(action.Params, "sha256"),
                                IsSourceMap = GetBoolean(action.Params, "sourceMap"),
                                RawJson = line,
                            });
                        }

                        break;
                    }

                    case "screencast-frame":
                        frames.Add(new TraceScreencastFrame
                        {
                            PageId = GetString(root, "pageId"),
                            File = GetString(root, "file"),
                            Width = GetInt32(root, "width"),
                            Height = GetInt32(root, "height"),
                            Timestamp = GetDouble(root, "timestamp"),
                            RawJson = line,
                        });
                        break;

                    case "":
                        break;

                    default:
                        warnings.Add("Unknown trace record type: " + type);
                        break;
                }
            }
            catch (JsonException ex)
            {
                warnings.Add("Malformed trace record: " + ex.Message);
            }
        }

        List<TraceNetworkEntry> network = new();
        string? networkText = null;
        if (isDirectory)
        {
            string networkFile = Path.Combine(sourcePath, "trace.network");
            if (File.Exists(networkFile))
            {
                networkText = File.ReadAllText(networkFile);
            }
        }
        else if (zipEntries!.TryGetValue("trace.network", out ZipArchiveEntry? networkEntry))
        {
            using Stream entryStream = networkEntry.Open();
            using StreamReader reader = new(entryStream, Encoding.UTF8);
            networkText = reader.ReadToEnd();
        }

        if (!string.IsNullOrEmpty(networkText))
        {
            foreach (string line in EnumerateLines(networkText))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("snapshot", out JsonElement snapshot))
                    {
                        continue;
                    }

                    JsonElement request = snapshot.TryGetProperty("request", out JsonElement requestElement)
                        ? requestElement
                        : default;
                    JsonElement response = snapshot.TryGetProperty("response", out JsonElement responseElement)
                        ? responseElement
                        : default;
                    JsonElement content = response.ValueKind == JsonValueKind.Object
                        && response.TryGetProperty("content", out JsonElement contentElement)
                            ? contentElement
                            : default;
                    network.Add(new TraceNetworkEntry
                    {
                        Method = GetString(request, "method"),
                        Url = GetString(request, "url"),
                        Status = GetInt32(response, "status"),
                        StatusText = GetString(response, "statusText"),
                        MimeType = GetString(content, "mimeType"),
                        StartedDateTime = GetString(snapshot, "startedDateTime"),
                        Time = GetDouble(snapshot, "time"),
                        BodySize = GetInt64(request, "bodySize"),
                        ResponseBodySize = GetInt64(response, "bodySize"),
                        FailureText = GetNullableString(snapshot, "_failureText"),
                        Snapshot = snapshot.Clone(),
                        RawJson = line,
                    });
                }
                catch (JsonException ex)
                {
                    warnings.Add("Malformed network record: " + ex.Message);
                }
            }
        }

        List<string> names = isDirectory
            ? files!.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToList()
            : zipEntries!.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToList();

        return new TraceArchive(
            sourcePath,
            isDirectory,
            stream,
            zip,
            zipEntries ?? new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal),
            files ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            Context = context,
            Actions = actions,
            ScreencastFrames = frames,
            Sources = sources,
            Network = network,
            EntryNames = names,
            Warnings = warnings,
        };
    }

    /// <summary>True when the archive contains an entry with exactly this name.</summary>
    public bool ContainsEntry(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = NormalizeName(name);
        lock (_gate)
        {
            return IsDirectory ? _files.ContainsKey(normalized) : _zipEntries.ContainsKey(normalized);
        }
    }

    /// <summary>Reads an arbitrary archive entry such as a screenshot or MHTML snapshot.</summary>
    public byte[] ReadEntryBytes(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = NormalizeName(name);
        lock (_gate)
        {
            if (IsDirectory)
            {
                if (!_files.TryGetValue(normalized, out string? file))
                {
                    throw new KeyNotFoundException("Trace entry was not found: " + normalized);
                }

                return File.ReadAllBytes(file);
            }

            if (!_zipEntries.TryGetValue(normalized, out ZipArchiveEntry? entry))
            {
                throw new KeyNotFoundException("Trace entry was not found: " + normalized);
            }

            using Stream stream = entry.Open();
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    /// <summary>Reads a text entry such as <c>trace.trace</c> or a source map.</summary>
    public string ReadEntryText(string name) => Encoding.UTF8.GetString(ReadEntryBytes(name));

    /// <summary>Reads the content for a captured JavaScript or CSS source.</summary>
    public string ReadSourceText(TraceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ReadEntryText(source.File);
    }

    /// <summary>
    /// Builds a self-contained HTML viewer with an action timeline, embedded screenshots, network
    /// entries and captured sources. Assets are inlined, so the result has no external references.
    /// </summary>
    public string CreateViewerHtml()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string title = WebUtility.HtmlEncode(Context?.Title ?? "ZeroCDP trace");
        StringBuilder html = new(16 * 1024);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>").Append(title).Append("</title><style>")
            .Append(":root{color-scheme:dark}body{margin:0;background:#12151b;color:#e6e9ef;font:14px/1.45 system-ui,sans-serif}")
            .Append("header{padding:20px 28px;background:#1b202a;border-bottom:1px solid #2c3442}")
            .Append("h1{margin:0 0 6px;font-size:22px}h2{margin:28px 0 10px;font-size:17px}")
            .Append("main{padding:0 28px 40px;max-width:1400px}.muted{color:#9aa6b8}")
            .Append(".cards{display:flex;gap:12px;flex-wrap:wrap;margin-top:16px}")
            .Append(".card{background:#1b202a;border:1px solid #2c3442;border-radius:8px;padding:10px 14px;min-width:120px}")
            .Append(".card b{display:block;font-size:20px}table{width:100%;border-collapse:collapse;font-size:13px}")
            .Append("th,td{padding:7px 9px;border-bottom:1px solid #252d3a;text-align:left;vertical-align:top}")
            .Append("th{color:#9aa6b8;font-weight:600}code,pre{font-family:ui-monospace,monospace;font-size:12px}")
            .Append("pre{white-space:pre-wrap;word-break:break-word;background:#161b23;padding:10px;border-radius:6px;max-height:420px;overflow:auto}")
            .Append(".shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px}")
            .Append("figure{margin:0;background:#1b202a;border:1px solid #2c3442;border-radius:8px;padding:8px}")
            .Append("img{width:100%;display:block;border-radius:4px;background:#000}")
            .Append("figcaption{padding-top:6px;color:#9aa6b8;font-size:12px}")
            .Append("details{background:#1b202a;border:1px solid #2c3442;border-radius:6px;margin:8px 0;padding:8px}")
            .Append("summary{cursor:pointer}a{color:#7cc4ff}.status-ok{color:#7fe0a8}.status-bad{color:#ff9b9b}")
            .Append("</style></head><body><header><h1>").Append(title).Append("</h1>")
            .Append("<div class=\"muted\">").Append(WebUtility.HtmlEncode(SourcePath)).Append("</div>")
            .Append("<div class=\"cards\"><div class=\"card\"><b>").Append(Actions.Count).Append("</b>actions</div>")
            .Append("<div class=\"card\"><b>").Append(ScreencastFrames.Count).Append("</b>frames</div>")
            .Append("<div class=\"card\"><b>").Append(Network.Count).Append("</b>requests</div>")
            .Append("<div class=\"card\"><b>").Append(Sources.Count).Append("</b>sources</div>")
            .Append("</div></header><main>");

        html.Append("<h2>Actions</h2><table><thead><tr><th>Time</th><th>Action</th><th>Page</th><th>Parameters</th></tr></thead><tbody>");
        foreach (TraceAction action in Actions)
        {
            html.Append("<tr><td>")
                .Append(action.Time.ToString("0.000", CultureInfo.InvariantCulture))
                .Append("s</td><td>").Append(WebUtility.HtmlEncode(action.Name))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(action.PageId ?? string.Empty))
                .Append("</td><td><code>")
                .Append(WebUtility.HtmlEncode(Truncate(action.Params.GetRawText(), 220)))
                .Append("</code></td></tr>");
        }

        html.Append("</tbody></table>");

        if (ScreencastFrames.Count > 0)
        {
            html.Append("<h2>Screenshots</h2><div class=\"shots\">");
            foreach (TraceScreencastFrame frame in ScreencastFrames)
            {
                string? data = TryReadDataUri(frame.File);
                if (data is null)
                {
                    continue;
                }

                html.Append("<figure><img loading=\"lazy\" src=\"").Append(data)
                    .Append("\" alt=\"\"><figcaption>")
                    .Append(WebUtility.HtmlEncode(frame.PageId))
                    .Append(" @ ").Append(frame.Timestamp.ToString("0.000", CultureInfo.InvariantCulture))
                    .Append("s &#183; ").Append(frame.Width).Append('x').Append(frame.Height)
                    .Append("</figcaption></figure>");
            }

            html.Append("</div>");
        }

        if (Network.Count > 0)
        {
            html.Append("<h2>Network</h2><table><thead><tr><th>Method</th><th>URL</th><th>Status</th><th>Type</th><th>Time</th></tr></thead><tbody>");
            foreach (TraceNetworkEntry entry in Network)
            {
                string statusClass = entry.Status is >= 200 and < 400 ? "status-ok" : "status-bad";
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(entry.Method))
                    .Append("</td><td><code>").Append(WebUtility.HtmlEncode(entry.Url))
                    .Append("</code></td><td class=\"").Append(statusClass).Append("\">")
                    .Append(entry.Status.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(WebUtility.HtmlEncode(entry.MimeType))
                    .Append("</td><td>").Append(entry.Time.ToString("0.000", CultureInfo.InvariantCulture))
                    .Append("ms</td></tr>");
                if (!string.IsNullOrEmpty(entry.FailureText))
                {
                    html.Append("<tr><td></td><td colspan=\"4\" class=\"status-bad\">")
                        .Append(WebUtility.HtmlEncode(entry.FailureText)).Append("</td></tr>");
                }
            }

            html.Append("</tbody></table>");
        }

        if (Sources.Count > 0)
        {
            html.Append("<h2>Sources</h2>");
            foreach (TraceSource source in Sources)
            {
                string content;
                try
                {
                    content = Truncate(ReadSourceText(source), 100_000);
                }
                catch (Exception ex) when (ex is KeyNotFoundException or IOException)
                {
                    content = "(not found in archive)";
                }

                html.Append("<details><summary><code>")
                    .Append(WebUtility.HtmlEncode(source.Url))
                    .Append(source.IsSourceMap ? " (source map)" : string.Empty)
                    .Append("</code></summary><pre>")
                    .Append(WebUtility.HtmlEncode(content))
                    .Append("</pre></details>");
            }
        }

        if (Warnings.Count > 0)
        {
            html.Append("<h2>Warnings</h2><ul>");
            foreach (string warning in Warnings)
            {
                html.Append("<li>").Append(WebUtility.HtmlEncode(warning)).Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    /// <summary>Writes <see cref="CreateViewerHtml"/> to a file or existing directory.</summary>
    public async ValueTask WriteViewerAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            fullPath = Path.Combine(fullPath, "trace-viewer.html");
        }
        else if (fullPath.EndsWith(Path.DirectorySeparatorChar)
            || fullPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            Directory.CreateDirectory(fullPath);
            fullPath = Path.Combine(fullPath, "trace-viewer.html");
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, CreateViewerHtml(), cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _zip?.Dispose();
        _stream?.Dispose();
    }

    private string? TryReadDataUri(string name)
    {
        try
        {
            byte[] bytes = ReadEntryBytes(name);
            string extension = Path.GetExtension(name).ToLowerInvariant();
            string mime = extension switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static JsonElement GetDetachedParams(JsonElement root)
    {
        if (root.TryGetProperty("params", out JsonElement parameters))
        {
            return parameters.Clone();
        }

        using JsonDocument empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? GetNullableString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int GetInt32(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.TryGetInt32(out int result)
                ? result
                : 0;

    private static long GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.TryGetInt64(out long result)
                ? result
                : 0;

    private static double GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.TryGetDouble(out double result)
                ? result
                : 0;

    private static bool GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();

    private static string NormalizeName(string name)
        => name.Replace('\\', '/').TrimStart('/');

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit] + "...";
}
