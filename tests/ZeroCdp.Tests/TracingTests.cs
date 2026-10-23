using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text;
using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class TracingTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => request.Path switch
            {
                "/style.css" => new TestHttpResponse
                {
                    ContentType = "text/css; charset=utf-8",
                    Body = "body{background:#fff}",
                },
                "/app.js" => new TestHttpResponse
                {
                    ContentType = "application/javascript; charset=utf-8",
                    Body = "window.traced = true;\n//# sourceMappingURL=app.js.map\n",
                },
                "/app.js.map" => new TestHttpResponse
                {
                    ContentType = "application/json; charset=utf-8",
                    Body = "{\"version\":3,\"sources\":[\"app.ts\"],\"mappings\":\"\"}",
                },
                _ => new TestHttpResponse
                {
                    Body = "<!doctype html><title>trace</title>"
                        + "<link rel=\"stylesheet\" href=\"/style.css\">"
                        + "<h1>traced page</h1>"
                        + "<script src=\"/app.js\"></script>",
                },
            },
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tracing_writes_trace_network_and_screencast_entries()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string path = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-trace-" + Guid.NewGuid().ToString("N") + ".zip");
        string chunkPath = path + ".chunk.zip";

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
                new LaunchOptions { Headless = true });
            BrowserContext context = await browser.Browser.NewContextAsync(
                new BrowserContextOptions { Viewport = new ViewportSize(320, 240) });

            try
            {
                await context.Tracing.StartAsync(
                    new TracingStartOptions { Title = "demo", Screenshots = true });

                CdpPage page = await context.NewPageAsync();
                await page.GotoAsync(_server!.Url("/trace"));
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(200));
                await page.EvaluateAsync(
                    "() => { document.body.style.background = '#111'; }");
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(150));
                await page.EvaluateAsync(
                    "() => { document.body.style.background = '#222'; }");
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(250));

                await context.Tracing.StopChunkAsync(chunkPath);
                Assert.True(File.Exists(chunkPath), chunkPath);

                using (ZipArchive chunk = ZipFile.OpenRead(chunkPath))
                {
                    string chunkTrace = ReadText(chunk, "trace.trace");
                    string chunkNetwork = ReadText(chunk, "trace.network");
                    Assert.Contains("\"type\":\"screencast-frame\"", chunkTrace, StringComparison.Ordinal);
                    Assert.True(
                        Count(chunkTrace, "\"type\":\"screencast-frame\"") >= 2,
                        "Expected continuous screencast frames in the chunk.");
                    Assert.Contains("\"method\":\"snapshot\"", chunkTrace, StringComparison.Ordinal);
                    Assert.Contains("\"method\":\"source\"", chunkTrace, StringComparison.Ordinal);
                    Assert.Contains(
                        chunk.Entries,
                        entry => entry.FullName.StartsWith("snapshots/", StringComparison.Ordinal));
                    Assert.Contains(
                        chunk.Entries,
                        entry => entry.FullName.StartsWith("sources/", StringComparison.Ordinal));
                    Assert.Contains(
                        chunk.Entries,
                        entry => entry.FullName.EndsWith(".map", StringComparison.Ordinal));
                    ZipArchiveEntry snapshot = chunk.Entries.First(
                        entry => entry.FullName.StartsWith("snapshots/", StringComparison.Ordinal));
                    Assert.Contains("MIME-Version", ReadEntryText(snapshot), StringComparison.Ordinal);
                    Assert.DoesNotContain("after=1", chunkNetwork, StringComparison.Ordinal);
                    Assert.Contains(
                        chunk.Entries,
                        entry => entry.FullName == "trace.trace");
                }

                await page.GotoAsync(_server.Url("/trace?after=1"));
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(200));
                await context.Tracing.StopAsync(path);

                Assert.True(File.Exists(path), path);
                using ZipArchive archive = ZipFile.OpenRead(path);
                Assert.Contains(archive.Entries, entry => entry.FullName == "trace.trace");
                Assert.Contains(archive.Entries, entry => entry.FullName == "trace.network");
                Assert.Contains(
                    archive.Entries,
                    entry => entry.FullName.StartsWith("screencast/", StringComparison.Ordinal));

                string trace = ReadText(archive, "trace.trace");
                Assert.Contains("\"type\":\"context-options\"", trace, StringComparison.Ordinal);
                Assert.Contains("\"type\":\"screencast-frame\"", trace, StringComparison.Ordinal);
                Assert.Contains("\"method\":\"snapshot\"", trace, StringComparison.Ordinal);
                Assert.Contains("\"page@", trace, StringComparison.Ordinal);

                string network = ReadText(archive, "trace.network");
                Assert.Contains("\"type\":\"resource-snapshot\"", network, StringComparison.Ordinal);
                Assert.Contains("/trace", network, StringComparison.Ordinal);

                using TraceArchive reader = TraceArchive.Open(path);
                Assert.Equal("demo", reader.Title);
                Assert.Contains(reader.Actions, action => action.Method == "snapshot");
                Assert.Contains(reader.Network, entry => entry.Url.Contains("/trace", StringComparison.Ordinal));
                Assert.NotEmpty(reader.ScreencastFrames);
                TraceScreencastFrame frame = reader.ScreencastFrames[0];
                Assert.NotEmpty(reader.ReadEntryBytes(frame.File));
                string viewer = reader.CreateViewerHtml();
                Assert.Contains("demo", viewer, StringComparison.Ordinal);
                Assert.Contains("data:image/jpeg;base64,", viewer, StringComparison.Ordinal);
                Assert.Contains("Actions", viewer, StringComparison.Ordinal);
                string viewerPath = Path.Combine(
                    Path.GetTempPath(),
                    "zerocdp-viewer-" + Guid.NewGuid().ToString("N") + ".html");
                try
                {
                    await reader.WriteViewerAsync(viewerPath);
                    Assert.True(File.Exists(viewerPath));
                }
                finally
                {
                    if (File.Exists(viewerPath))
                    {
                        File.Delete(viewerPath);
                    }
                }
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(chunkPath))
            {
                File.Delete(chunkPath);
            }
        }
    }

    [Fact]
    public async Task Tracing_attaches_to_a_page_that_already_exists()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string path = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-trace-existing-" + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
                new LaunchOptions { Headless = true });
            BrowserContext context = await browser.Browser.NewContextAsync(
                new BrowserContextOptions { Viewport = new ViewportSize(320, 240) });

            try
            {
                CdpPage page = await context.NewPageAsync();
                await page.GotoAsync(_server!.Url("/trace"));

                await context.Tracing.StartAsync(
                    new TracingStartOptions { Title = "existing", Snapshots = false });
                await page.EvaluateAsync(
                    "() => { document.body.style.background = '#345'; }");
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(300));
                await context.Tracing.StopAsync(path);

                using ZipArchive archive = ZipFile.OpenRead(path);
                string trace = ReadText(archive, "trace.trace");
                Assert.Contains("\"type\":\"screencast-frame\"", trace, StringComparison.Ordinal);
                Assert.Contains("\"page@", trace, StringComparison.Ordinal);
                Assert.DoesNotContain("\"method\":\"snapshot\"", trace, StringComparison.Ordinal);
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Trace_archive_reads_an_extracted_directory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-trace-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "trace.trace"),
                "{\"type\":\"context-options\",\"version\":8,\"title\":\"directory\",\"origin\":\"library\"}\n"
                + "{\"type\":\"event\",\"time\":0.5,\"class\":\"Frame\",\"method\":\"navigated\",\"params\":{\"url\":\"https://example.test/\"},\"pageId\":\"page@1\"}\n"
                + "{\"type\":\"screencast-frame\",\"pageId\":\"page@1\",\"file\":\"screencast/frame.jpg\",\"width\":10,\"height\":20,\"timestamp\":0.6}\n");
            File.WriteAllText(
                Path.Combine(directory, "trace.network"),
                "{\"type\":\"resource-snapshot\",\"snapshot\":{\"startedDateTime\":\"2026-01-01T00:00:00Z\",\"time\":12,\"request\":{\"method\":\"GET\",\"url\":\"https://example.test/\"},\"response\":{\"status\":200,\"statusText\":\"OK\",\"content\":{\"mimeType\":\"text/html\"},\"bodySize\":7}}}\n");
            Directory.CreateDirectory(Path.Combine(directory, "screencast"));
            File.WriteAllBytes(
                Path.Combine(directory, "screencast", "frame.jpg"),
                new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            using TraceArchive archive = TraceArchive.Open(directory);
            Assert.True(archive.IsDirectory);
            Assert.Equal("directory", archive.Title);
            Assert.Single(archive.Actions);
            Assert.Single(archive.ScreencastFrames);
            Assert.Single(archive.Network);
            Assert.Equal(200, archive.Network[0].Status);
            Assert.Equal("text/html", archive.Network[0].MimeType);
            Assert.Contains("screencast/frame.jpg", archive.EntryNames);
            Assert.NotEmpty(archive.ReadEntryBytes("screencast/frame.jpg"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReadText(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            item => item.FullName == name);
        return ReadEntryText(entry);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
