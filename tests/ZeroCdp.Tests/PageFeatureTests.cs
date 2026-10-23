using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public class PageFeatureTests : IAsyncLifetime
{
    private LocalHttpServer? _server;
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = request => new TestHttpResponse
            {
                Body = $"<!doctype html><title>{request.Path}</title><div id='path'>{request.Path}</div>",
            },
        };
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-page-feature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task New_cdp_session_is_independent_and_detachable()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        CdpPage page = await context.NewPageAsync();

        CdpSession session = await context.NewCDPSessionAsync(page);
        Assert.NotEqual(page.Session.SessionId, session.SessionId);
        Assert.Equal(page.TargetId, session.TargetId);

        FrameTreeResult tree = await session.SendAsync<FrameTreeResult>(
            "Page.getFrameTree"u8);
        Assert.Equal(page.MainFrameId, tree.MainFrameId);

        await session.DetachAsync();
        await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(page.IsClosed);
        Assert.Equal(string.Empty, await page.TitleAsync());
    }

    [Fact]
    public async Task Context_exposes_a_service_worker_and_evaluates_in_it()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        _server!.Handler = request => request.Path switch
        {
            "/sw.js" => new TestHttpResponse
            {
                ContentType = "text/javascript",
                Body = "self.addEventListener('install', e => self.skipWaiting());",
            },
            "/sw-page" => new TestHttpResponse
            {
                Body = "<!doctype html><script>" +
                    "navigator.serviceWorker.register('/sw.js');" +
                    "</script>",
            },
            _ => new TestHttpResponse { Body = "unexpected" },
        };

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync();
        TaskCompletionSource<ServiceWorker> observed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.ServiceWorker += worker => observed.TrySetResult(worker);
        CdpPage page = await context.NewPageAsync();

        await page.GotoAsync(_server.Url("/sw-page"));
        ServiceWorker serviceWorker = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(serviceWorker, context.ServiceWorkers);
        Assert.Contains("/sw.js", serviceWorker.Url, StringComparison.Ordinal);
        Assert.False(serviceWorker.IsClosed);

        EvaluationResult result = await serviceWorker.EvaluateAsync("2 + 3");
        Assert.True(result.Value.TryGetInt64(out long value));
        Assert.Equal(5, value);
    }

    [Fact]
    public async Task Context_records_har_with_request_response_and_body()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string harPath = Path.Combine(_directory, "session.har");
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        BrowserContext context = await browser.Browser.NewContextAsync(
            new BrowserContextOptions { RecordHarPath = harPath });
        CdpPage page = await context.NewPageAsync();

        await page.GotoAsync(_server!.Url("/har"));
        await context.CloseAsync();

        Assert.True(File.Exists(harPath));
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(harPath));
        JsonElement entries = document.RootElement
            .GetProperty("log")
            .GetProperty("entries");
        JsonElement entry = entries
            .EnumerateArray()
            .First(item => item
                .GetProperty("request")
                .GetProperty("url")
                .GetString()!
                .Contains("/har", StringComparison.Ordinal));

        Assert.Equal(200, entry.GetProperty("response").GetProperty("status").GetInt32());
        Assert.Contains(
            "/har",
            entry.GetProperty("response").GetProperty("content").GetProperty("text").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_input_files_works_for_hidden_file_inputs()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string upload = Path.Combine(_directory, "upload.txt");
        await File.WriteAllTextAsync(upload, "payload");

        string file = Path.Combine(_directory, "upload.html");
        await File.WriteAllTextAsync(
            file,
            """
            <!doctype html>
            <input id="file" type="file" style="display:none">
            <script>
              window.fileNames = [];
              document.getElementById('file').addEventListener('change', event => {
                window.fileNames = Array.from(event.target.files).map(file => file.name + ':' + file.size);
              });
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator input = page.Locator("#file");
        await input.SetInputFilesAsync(upload);

        EvaluationResult name = await page.EvaluateAsync("document.getElementById('file').files[0].name");
        Assert.Equal("upload.txt", name.Value.StringValue);
        EvaluationResult size = await page.EvaluateAsync("document.getElementById('file').files[0].size");
        Assert.True(size.Value.TryGetInt64(out long fileSize));
        Assert.Equal(7, fileSize);

        await input.SetInputFilesAsync(Array.Empty<string>(), CancellationToken.None);
        EvaluationResult count = await page.EvaluateAsync("document.getElementById('file').files.length");
        Assert.True(count.Value.TryGetInt64(out long fileCount));
        Assert.Equal(0, fileCount);
    }

    [Fact]
    public async Task Response_exposes_security_details_for_https()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        using RSA rsa = RSA.Create(2048);
        CertificateRequest certificateRequest = new(
            "CN=127.0.0.1",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder san = new();
        san.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(san.Build());
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        await using LocalHttpServer server = new()
        {
            Certificate = certificate,
            Handler = _ => new TestHttpResponse { Body = "<!doctype html><title>secure</title>" },
        };
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions
            {
                Headless = true,
                AdditionalArguments = ["--ignore-certificate-errors"],
            });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.EnableNetworkAsync();

        TaskCompletionSource<PageResponse> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Response += response =>
        {
            if (response.Url.Contains("/secure", StringComparison.Ordinal))
            {
                observed.TrySetResult(response);
            }
        };

        string url = server.Url("/secure").Replace("http://", "https://", StringComparison.Ordinal);
        await page.GotoAsync(url);
        PageResponse response = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        SecurityDetails? details = response.SecurityDetails;
        Assert.NotNull(details);
        Assert.Contains("127.0.0.1", details.SubjectName, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(details.Protocol));
        Assert.True(details.ValidTo > details.ValidFrom);
        Assert.Contains("127.0.0.1", details.SanList);
    }

    [Fact]
    public async Task Redirect_chain_sizes_and_server_address_are_exposed()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        _server!.Handler = null;
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        List<PageRequest> requests = new();
        List<PageResponse> responses = new();
        page.Request += request =>
        {
            lock (requests)
            {
                requests.Add(request);
            }
        };
        page.Response += response =>
        {
            lock (responses)
            {
                responses.Add(response);
            }
        };

        await page.GotoAsync(_server.Url("/redirect"));

        PageRequest redirect;
        PageRequest hello;
        lock (requests)
        {
            redirect = requests.First(request => request.Url.EndsWith("/redirect", StringComparison.Ordinal));
            hello = requests.First(request => request.Url.EndsWith("/hello", StringComparison.Ordinal));
        }

        Assert.Same(redirect, hello.RedirectedFrom);
        Assert.Same(hello, redirect.RedirectedTo);
        Assert.Equal(302, hello.RedirectResponseStatus);

        PageResponse response = await hello.ResponseAsync().WaitAsync(TimeSpan.FromSeconds(10))
            ?? throw new Xunit.Sdk.XunitException("The final response was not observed.");
        Assert.Equal(200, response.Status);
        Assert.True((await hello.SizesAsync()).ResponseBodySize > 0);
        ServerAddr? address = await response.ServerAddrAsync();
        Assert.NotNull(address);
        Assert.Equal("127.0.0.1", address.Value.IpAddress);
    }

    [Fact]
    public async Task Context_permissions_enable_geolocation()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        await using BrowserContext context = await browser.Browser.NewContextAsync(new BrowserContextOptions
        {
            Geolocation = new Geolocation(40.7128, -74.0060, 5),
        });

        await context.GrantPermissionsAsync("geolocation", _server!.Url("/"));
        CdpPage page = await context.NewPageAsync(_server.Url("/geo"));
        await page.WaitForLoadStateAsync();

        EvaluationResult result = await page.EvaluateAsync(
            """
            new Promise(resolve => {
              navigator.geolocation.getCurrentPosition(
                position => resolve(position.coords.latitude.toFixed(4) + ',' + position.coords.longitude.toFixed(4)),
                error => resolve('error:' + error.code + ':' + error.message));
            })
            """);

        Assert.Equal("40.7128,-74.0060", result.Value.StringValue);

        await context.ClearPermissionsAsync();
    }
}
