using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;

// A NativeAOT console app: no reflection, no dynamic code, no serializer.
// Publish with: dotnet publish -c Release -r linux-x64

await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
{
    Headless = true,
});

Console.WriteLine($"transport : {browser.Connection.TransportDescription}");

BrowserVersion version = await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8);
Console.WriteLine($"product   : {Encoding.UTF8.GetString(version.Product.Span)}");
Console.WriteLine($"protocol  : {Encoding.UTF8.GetString(version.ProtocolVersion.Span)}");

// A local file keeps the sample offline and still exercises a real navigation.
string pagePath = Path.Combine(Path.GetTempPath(), "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".html");
string uploadPath = Path.Combine(Path.GetTempPath(), "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".txt");
await File.WriteAllTextAsync(
    pagePath,
    """
    <!doctype html>
    <html><head><title>ZeroCDP sample</title></head>
    <body>
      <p>hello from AOT</p>
      <ul><li class="item">one</li><li class="item">two</li></ul>
      <input id="name" oninput="document.title='typed:'+this.value">
      <input id="upload" type="file" style="display:none">
      <select id="choice" onchange="document.title='selected:'+this.value">
        <option value="a">Alpha</option>
        <option value="b">Beta</option>
      </select>
      <button id="alert" onclick="alert('hi'); document.title='alert-dismissed'">Alert</button>
      <button id="go" onclick="document.title='clicked'">Go</button>
      <button id="popup">Popup</button>
      <button id="download">Download</button>
      <button id="pick">Pick file</button>
      <div id="host"></div>
      <iframe id="frame" srcdoc="<body><p id='frame-result'>frame-ready</p></body>"></iframe>
      <script>
        window.mainWorldMarker = 'only-in-main-world';
        const root = document.getElementById('host').attachShadow({ mode: 'closed' });
        root.innerHTML = '<button id="inner">shadow</button>';
        root.getElementById('inner').addEventListener('click', () => { document.title = 'shadow-clicked'; });
        document.getElementById('popup').addEventListener('click', () => window.open('about:blank', '_blank'));
        document.getElementById('pick').addEventListener('click', () => document.getElementById('upload').click());
        document.getElementById('download').addEventListener('click', () => {
          const url = URL.createObjectURL(new Blob(['download payload'], { type: 'text/plain' }));
          const anchor = document.createElement('a');
          anchor.href = url;
          anchor.download = 'sample.txt';
          anchor.click();
          setTimeout(() => URL.revokeObjectURL(url), 1000);
        });
      </script>
    </body></html>
    """);
await File.WriteAllTextAsync(uploadPath, "aot upload");

try
{
    // P1: an isolated context owns its own cookies/cache and applies emulation before navigation.
    await using BrowserContext context = await browser.Browser.NewContextAsync(new BrowserContextOptions
    {
        Viewport = new ViewportSize(1024, 768),
        ColorScheme = PreferredColorScheme.Dark,
        Locale = "en-US",
    });

    await using CdpPage page = await context.NewPageAsync();

    // P1: intercept one request and answer it without opening a network socket.
    await page.RouteAsync(
        "https://sample.invalid/**",
        async route => await route.FulfillAsync(
            200,
            """{"value":42}""",
            "application/json; charset=utf-8",
            [new CdpHeader("access-control-allow-origin", "*")]));

    GotoResult navigation = await page.GotoAsync(new Uri(pagePath).AbsoluteUri);
    Console.WriteLine($"navigated : loader {navigation.LoaderId}");
    Console.WriteLine($"title     : {await page.TitleAsync()}");
    Console.WriteLine($"url       : {await page.UrlAsync()}");

    string content = await page.ContentAsync();
    Console.WriteLine($"content   : {content.Length} chars, contains marker: {content.Contains("hello from AOT", StringComparison.Ordinal)}");

    Locator items = page.Locator(".item");
    Console.WriteLine($"locator   : count={await items.CountAsync()} text=[{string.Join(',', await items.AllTextContentsAsync())}] last={await items.Last().TextAsync()}");
    Console.WriteLine($"chained   : {await page.Locator("ul").Locator(".item").Nth(1).TextAsync()}");
    Console.WriteLine($"semantic  : {await page.GetByText("hello from AOT").TextAsync()}");

    // WebSocket observation is opt-in Network-domain traffic; no script is injected into the page.
    ValueTask<WebSocketInfo> webSocketWait = page.WaitForWebSocketAsync();
    await page.EvaluateAsync("void new WebSocket('ws://127.0.0.1:1/')");
    WebSocketInfo webSocket = await webSocketWait;
    Console.WriteLine($"websocket : {webSocket.Url}");

    BoundingBox? goBox = await page.Locator("#go").BoundingBoxAsync();
    Console.WriteLine($"box       : {goBox?.Width:0}x{goBox?.Height:0}");
    await using (ElementHandle goHandle = (await page.QuerySelectorAsync("#go"))!)
    {
        Console.WriteLine($"handle    : {await goHandle.TextContentAsync()}");
        await page.Expect(page.Locator("#go")).ToBeVisibleAsync();
    }

    await page.Locator("#name").FocusAsync();
    EvaluationResult focused = await page.EvaluateAsync("document.activeElement.id");
    Console.WriteLine($"focus     : {focused.Value.StringValue}");

    await page.GetByRole("button", "Go").ClickAsync();
    Console.WriteLine($"click     : {await WaitForTitleAsync(page, "clicked")}");

    EvaluationResult mocked = await page.EvaluateAsync(
        "fetch('https://sample.invalid/data').then(r => r.json()).then(j => j.value)");
    Console.WriteLine($"route     : mocked value = {Encoding.UTF8.GetString(mocked.Value.ValueJson.Span)}");

    // JavaScript runs in an isolated world: page globals are invisible, the DOM is shared, and
    // the Runtime domain is never enabled. Main-world evaluation is an explicit Patchright opt-in.
    EvaluationResult sum = await page.EvaluateAsync("2 + 2");
    Console.WriteLine($"eval      : 2 + 2 = {Encoding.UTF8.GetString(sum.Value.ValueJson.Span)}");
    EvaluationResult marker = await page.EvaluateAsync("typeof window.mainWorldMarker");
    Console.WriteLine($"isolated  : main-world marker visible? {marker.Value.StringValue} (expected undefined)");
    EvaluationResult mainMarker = await page.EvaluateAsync(
        "window.mainWorldMarker",
        isolatedContext: false);
    Console.WriteLine($"main world: marker = {mainMarker.Value.StringValue} (opt-in)");

    // A closed shadow root is now reached by a normal locator with a throttled pierced-snapshot
    // fallback, matching Patchright. The explicit pierce flag remains available.
    Console.WriteLine($"shadow    : normal locator count = {await page.Locator("#inner").CountAsync()}");
    await page.Locator("#inner").ClickAsync();
    Console.WriteLine($"shadow    : {await WaitForTitleAsync(page, "shadow-clicked")}");

    // Same-process frame locators use the frame's isolated world, including closed roots.
    FrameLocator sampleFrame = page.FrameLocator("#frame");
    Console.WriteLine($"frame     : {await sampleFrame.Locator("#frame-result").TextAsync()}");

    // Popups are registered as pages in the same context and expose the normal page surface.
    CdpPage popup = await page.RunAndWaitForPopupAsync(
        () => page.Locator("#popup").ClickAsync());
    await popup.SetContentAsync("<p id='popup-content'>popup-ready</p>");
    Console.WriteLine($"popup     : {await popup.Locator("#popup-content").TextAsync()}");
    await popup.CloseAsync();

    // P0/P1 surface: trusted fill/select/upload, page and element screenshots, dialog dismissal.
    await page.Locator("#name").FillAsync("aot");
    Console.WriteLine($"fill      : {await WaitForTitleAsync(page, "typed:aot")}");

    string[] selected = await page.Locator("#choice").SelectOptionAsync("b");
    Console.WriteLine($"select    : {string.Join(',', selected)} / {await WaitForTitleAsync(page, "selected:b")}");

    await page.Locator("#upload").SetInputFilesAsync(uploadPath);
    EvaluationResult uploaded = await page.EvaluateAsync("document.getElementById('upload').files[0].name");
    Console.WriteLine($"upload    : {uploaded.Value.StringValue}");

    string screenshotPath = Path.Combine(
        Path.GetTempPath(),
        "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".png");
    try
    {
        await page.ScreenshotAsync(screenshotPath, new ScreenshotOptions { FullPage = true });
        Console.WriteLine($"screenshot: {new FileInfo(screenshotPath).Length} bytes");

        byte[] elementPng = await page.Locator("#go").ScreenshotBytesAsync();
        Console.WriteLine($"element   : {elementPng.Length} bytes");
    }
    finally
    {
        File.Delete(screenshotPath);
    }

    FileChooser chooser = await page.RunAndWaitForFileChooserAsync(
        () => page.Locator("#pick").ClickAsync());
    await chooser.AcceptAsync(uploadPath);
    EvaluationResult chosen = await page.EvaluateAsync("document.getElementById('upload').files[0].name");
    Console.WriteLine($"chooser   : {chosen.Value.StringValue}");

    Download download = await page.RunAndWaitForDownloadAsync(
        () => page.Locator("#download").ClickAsync());
    await download.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
    string savedDownload = Path.Combine(
        Path.GetTempPath(),
        "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".txt");
    try
    {
        await download.SaveAsAsync(savedDownload);
        Console.WriteLine($"download  : {new FileInfo(savedDownload).Length} bytes");
    }
    finally
    {
        await download.DeleteAsync();
        File.Delete(savedDownload);
    }

    await page.Locator("#alert").ClickAsync();
    Console.WriteLine($"dialog    : {await WaitForTitleAsync(page, "alert-dismissed")}");

    // Context routes apply to pages created later, including their first navigation. Context
    // request/response observation spans every page in the context.
    await context.RouteAsync(
        "https://context.invalid/**",
        route => route.FulfillAsync(
            200,
            "<p id='context-body'>context-route</p>",
            "text/html; charset=utf-8"));
    await using CdpPage routed = await context.NewPageAsync();
    Task<PageResponse> observed = context.WaitForResponseAsync("**/context").AsTask();
    await routed.GotoAsync("https://context.invalid/context");
    PageResponse routedResponse = await observed;
    Console.WriteLine(
        $"context   : {routedResponse.Status} / {await routed.Locator("#context-body").TextAsync()}");

    // Storage state is Playwright-compatible JSON, written and parsed without a serializer.
    await routed.EvaluateAsync("localStorage.setItem('sample-key', 'sample-value')");
    StorageState state = await context.StorageStateAsync();
    string statePath = Path.Combine(
        Path.GetTempPath(),
        "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        await state.SaveAsync(statePath);
        StorageState loadedState = await StorageState.LoadAsync(statePath);
        Console.WriteLine(
            $"storage   : {loadedState.Origins.Count} origin(s), {loadedState.Cookies.Count} cookie(s)");
    }
    finally
    {
        File.Delete(statePath);
    }

    await routed.EvaluateAsync(
        "setTimeout(() => { document.body.dataset.ready = 'yes'; }, 20)");
    await routed.WaitForFunctionAsync("document.body.dataset.ready === 'yes'");
    Console.WriteLine("wait      : function predicate became truthy");

    // Tracing exercises client-side PNG capture, MHTML snapshots and the zip writer without
    // pulling in reflection-based serialization.
    string tracePath = Path.Combine(
        Path.GetTempPath(),
        "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".zip");
    try
    {
        await context.Tracing.StartAsync(
            new TracingStartOptions
            {
                Title = "aot",
                Sources = false,
                Snapshots = true,
                Screenshots = true,
            });
        await routed.EvaluateAsync(
            "() => { document.body.style.background = '#246'; }");
        await Task.Delay(150);
        await context.Tracing.StopAsync(tracePath);
        Console.WriteLine($"trace     : {new FileInfo(tracePath).Length} bytes");

        string viewerPath = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-sample-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            using TraceArchive archive = TraceArchive.Open(tracePath);
            await archive.WriteViewerAsync(viewerPath);
            Console.WriteLine(
                $"trace read: {archive.Actions.Count} actions, {archive.Network.Count} requests, " +
                $"{archive.ScreencastFrames.Count} frames, viewer {new FileInfo(viewerPath).Length} bytes");
        }
        finally
        {
            File.Delete(viewerPath);
        }
    }
    finally
    {
        File.Delete(tracePath);
    }
}
finally
{
    File.Delete(pagePath);
    File.Delete(uploadPath);
}

static async Task<string> WaitForTitleAsync(CdpPage page, string expected)
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        string title = await page.TitleAsync();
        if (title == expected)
        {
            return title;
        }

        await Task.Delay(20);
    }

    return await page.TitleAsync();
}
