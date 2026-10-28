using System.Diagnostics.CodeAnalysis;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class DownloadAndFileChooserTests : IAsyncLifetime
{
    private LocalHttpServer? _server;
    private string? _directory;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => request.Path switch
            {
                "/download" => new TestHttpResponse
                {
                    Body = """
                        <!doctype html>
                        <title>download</title>
                        <a id="get" href="/payload" download="report.txt">get file</a>
                        """,
                },
                "/payload" => new TestHttpResponse
                {
                    ContentType = "application/octet-stream",
                    Body = "downloaded payload",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Content-Disposition"] = "attachment; filename=\"report.txt\"",
                    },
                },
                "/chooser" => new TestHttpResponse
                {
                    Body = """
                        <!doctype html>
                        <title>chooser</title>
                        <input id="file" type="file" multiple>
                        <button id="pick" onclick="document.getElementById('file').click()">pick</button>
                        """,
                },
                _ => new TestHttpResponse { Body = "<!doctype html><title>other</title>" },
            },
        };

        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Download_is_captured_and_can_be_saved()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/download"));

        Download? observed = null;
        page.DownloadOpening += download => observed = download;

        Download download = await page.RunAndWaitForDownloadAsync(
            () => page.Locator("#get").ClickAsync(),
            timeout: TimeSpan.FromSeconds(15));

        Assert.Same(download, observed);
        Assert.Equal("report.txt", download.SuggestedFilename);
        Assert.EndsWith("/payload", download.Url, StringComparison.Ordinal);
        Assert.StartsWith(browser.Browser.DownloadsDirectory, download.Path, StringComparison.Ordinal);

        await download.WaitForCompletionAsync(TimeSpan.FromSeconds(15));
        Assert.True(download.IsCompleted);
        Assert.False(download.IsCanceled);
        Assert.True(File.Exists(download.Path));
        Assert.Equal("downloaded payload", await File.ReadAllTextAsync(download.Path));
        Assert.True(download.ReceivedBytes > 0);
        Assert.True(download.TotalBytes >= download.ReceivedBytes);
        Assert.Null(await download.FailureAsync());
        Assert.Equal(download.Path, await download.PathAsync());
        Assert.Equal("report.txt", await download.SuggestedFilenameAsync());
        Assert.EndsWith("/payload", await download.UrlAsync(), StringComparison.Ordinal);

        await using (Stream stream = await download.CreateReadStreamAsync())
        {
            using StreamReader reader = new(stream);
            Assert.Equal("downloaded payload", await reader.ReadToEndAsync());
        }

        await download.CancelAsync();
        Assert.True(download.IsCompleted);

        string destination = Path.Combine(_directory!, "saved.txt");
        await download.SaveAsAsync(destination, overwrite: true);
        Assert.Equal("downloaded payload", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task File_chooser_can_select_multiple_files()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string first = Path.Combine(_directory!, "first.txt");
        string second = Path.Combine(_directory!, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/chooser"));

        FileChooser? observed = null;
        page.FileChooserOpening += chooser => observed = chooser;

        FileChooser fileChooser = await page.RunAndWaitForFileChooserAsync(
            () => page.Locator("#pick").ClickAsync(),
            timeout: TimeSpan.FromSeconds(15));

        Assert.Same(fileChooser, observed);
        Assert.True(fileChooser.IsMultiple);
        Assert.Equal(page.TargetId, fileChooser.Page.TargetId);

        await fileChooser.AcceptAsync(new[] { first, second });

        EvaluationResult result = await page.EvaluateAsync(
            "document.getElementById('file').files.length");
        Assert.True(result.Value.TryGetInt64(out long count));
        Assert.Equal(2, count);

        string? inputValue = await page.Locator("#file").InputValueAsync();
        Assert.Contains("first.txt", inputValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_chooser_accepts_in_memory_payloads()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/chooser"));

        FileChooser fileChooser = await page.RunAndWaitForFileChooserAsync(
            () => page.Locator("#pick").ClickAsync(),
            timeout: TimeSpan.FromSeconds(15));

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("memory payload");
        await fileChooser.SetFilesAsync(
            [new FilePayload { Name = "memory.txt", MimeType = "text/plain", Buffer = payload }]);

        EvaluationResult name = await page.EvaluateAsync(
            "document.getElementById('file').files[0].name");
        Assert.Equal("memory.txt", name.Value.StringValue);
        EvaluationResult text = await page.EvaluateAsync(
            "document.getElementById('file').files[0].text()");
        Assert.Equal("memory payload", text.Value.StringValue);
    }

    [Fact]
    public async Task File_chooser_can_be_canceled()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(_server!.Url("/chooser"));

        FileChooser fileChooser = await page.RunAndWaitForFileChooserAsync(
            () => page.Locator("#pick").ClickAsync(),
            timeout: TimeSpan.FromSeconds(15));

        await fileChooser.CancelAsync();

        EvaluationResult result = await page.EvaluateAsync(
            "document.getElementById('file').files.length");
        Assert.True(result.Value.TryGetInt64(out long count));
        Assert.Equal(0, count);
    }
}
