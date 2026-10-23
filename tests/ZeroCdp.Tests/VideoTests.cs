using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class VideoTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = _ => new TestHttpResponse
            {
                Body = "<!doctype html><title>video</title><body style='background:#c33'><h1>recording</h1></body>",
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
    public async Task Video_records_webm_and_supports_save_delete()
    {
        if (BrowserLocator.Find() is null || VideoRecorder.FindFfmpeg() is null)
        {
            return;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
                new LaunchOptions { Headless = true });

            BrowserContext context = await browser.Browser.NewContextAsync(
                new BrowserContextOptions
                {
                    Viewport = new ViewportSize(320, 240),
                    RecordVideo = new RecordVideoOptions
                    {
                        Dir = directory,
                        Size = new ViewportSize(320, 240),
                        FfmpegPath = VideoRecorder.FindFfmpeg(),
                    },
                });

            try
            {
                CdpPage page = await context.NewPageAsync();
                Assert.NotNull(page.Video);
                await page.GotoAsync(_server!.Url("/video"));
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(700));
                await page.CloseAsync();

                string path = await page.Video!.PathAsync();
                Assert.True(File.Exists(path), path);
                Assert.True(new FileInfo(path).Length > 0, "The video file is empty.");
                Assert.Equal(".webm", Path.GetExtension(path), ignoreCase: true);

                string copy = Path.Combine(directory, "copy.webm");
                await page.Video.SaveAsAsync(copy);
                Assert.True(File.Exists(copy));

                await page.Video.DeleteAsync();
                Assert.False(File.Exists(path));
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task Video_and_tracing_share_one_screencast()
    {
        if (BrowserLocator.Find() is null || VideoRecorder.FindFfmpeg() is null)
        {
            return;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-video-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string tracePath = Path.Combine(directory, "trace.zip");

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
                new LaunchOptions { Headless = true });

            BrowserContext context = await browser.Browser.NewContextAsync(
                new BrowserContextOptions
                {
                    Viewport = new ViewportSize(320, 240),
                    RecordVideo = new RecordVideoOptions
                    {
                        Dir = directory,
                        Size = new ViewportSize(320, 240),
                        FfmpegPath = VideoRecorder.FindFfmpeg(),
                    },
                });

            try
            {
                await context.Tracing.StartAsync(new TracingStartOptions { Snapshots = false });
                CdpPage page = await context.NewPageAsync();
                await page.GotoAsync(_server!.Url("/video"));
                await page.EvaluateAsync("() => { document.body.style.background = '#123'; }");
                await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(300));
                await context.Tracing.StopAsync(tracePath);
                await page.CloseAsync();

                string videoPath = await page.Video!.PathAsync();
                Assert.True(File.Exists(videoPath), videoPath);
                Assert.True(new FileInfo(videoPath).Length > 0, "The video file is empty.");

                using ZipArchive archive = ZipFile.OpenRead(tracePath);
                Assert.Contains(
                    archive.Entries,
                    entry => entry.FullName.StartsWith("screencast/", StringComparison.Ordinal));
            }
            finally
            {
                await context.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
