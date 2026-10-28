# ZeroCDP

[Tiếng Việt](README.vi.md) · [English](README.md)

Thư viện client Chrome DevTools Protocol cho .NET 10, với lõi dispatch **zero-allocation**, hỗ trợ
**NativeAOT**, và tư thế **stealth theo phong cách Patchright** được tự implement lại từ đầu —
không dùng runtime reflection, không serializer, không `dynamic`.

ZeroCDP giao tiếp với trình duyệt họ Chromium (Chrome, Chromium, Edge) qua
`--remote-debugging-pipe` theo mặc định trên Linux, macOS và Windows, nên **không mở cổng TCP
debugger nào**. POSIX dùng fd 3/4 của tiến trình con; Windows truyền hai handle anonymous-pipe
kế thừa qua `--remote-debugging-io-pipes`. `CdpTransportKind.Port` vẫn sẵn có cho ai cần endpoint
WebSocket loopback. Việc chạy JavaScript dùng **isolated world** và **không bao giờ** bật domain
Runtime. Lần duy nhất có thể bật Runtime theo ý muốn là `RouteWebSocketAsync` (transport console),
được ghi rõ riêng.

Gói hiện tại là [**ZeroCdp** trên NuGet](https://www.nuget.org/packages/ZeroCdp) phiên bản
**0.1.0-alpha**. API dùng được để đánh giá, nhưng các bản minor vẫn có thể thay đổi mang tính bổ
sung hoặc chỉnh sửa.

## Cài đặt

```bash
dotnet add package ZeroCdp
```

Hoặc ghim rõ phiên bản:

```bash
dotnet add package ZeroCdp --version 0.1.0-alpha
```

Gói target `net10.0`, trimmable hoàn toàn (analyzer `IsTrimmable`, `IsAotCompatible` được bật nghiêm
ngặt lúc build) và chỉ phụ thuộc BCL.

## Trạng thái

| Mốc | Trạng thái | Ý nghĩa |
|---|---|---|
| M0 | ✅ | Khung dự án, analyzer AOT, publish thử NativeAOT |
| M1 | ✅ | Pipe transport (POSIX fd 3/4, inherited handle trên Windows), framing NUL, bảng lệnh, reader loop |
| M2 | ✅ | Flattened session, auto-attach target, demultiplexing theo session |
| M3 | ✅ | Điều hướng, chờ lifecycle, title/url/content, cookie |
| M4 | ✅ | CSS engine, locator, event input thật, closed shadow root |
| M5 | ✅ | Isolated world, không `Runtime.enable`, launch profile, trace harness |
| M6 | ✅ | Redaction, giới hạn tài nguyên, command timeout, allow-list điều hướng |
| M7 | ✅ | Benchmark, đóng gói, tài liệu, sample AOT |
| P0 | ✅ | Screenshot, dialog, iframe cùng tiến trình, auto-wait locator, fill/check/hover/select/press |
| P1 | ✅ | Browser context, route/observe network, lịch sử điều hướng, locator ngữ nghĩa, state/box/focus, upload, permission, screenshot phần tử, tinh chỉnh Patchright |
| P2 | ✅ | Popup, download, file chooser, storage state, context route/event, closed-shadow + ngữ nghĩa trong frame, chỉnh sửa/evaluate/drag locator, wait helper |
| P3 | ✅ | Parity selector, XPath, actionability hit-target/stable-box, NetworkIdle, keyboard/mouse, helper network của context, binding, init script, connectOverCDP |
| P4 | ✅ | OOPIF (kể cả lồng nhau), routing child-session, synthetic node id, compose transform toạ độ, options/enums kiểu Playwright, sample thực tế chạy được |
| P5 | 🚧 | HAR record/replay, ARIA snapshot, Clock, storage state IndexedDB, tracing liên tục/theo chunk, DOM snapshot và source map, video |
| P6 | ✅ | Assert kiểu web-first, handle JS/Element, APIRequestContext độc lập và gắn context, coverage JS/CSS, parity option context, HTTP auth, route WebSocket, reader/viewer trace archive |

Gate T0 là một khẳng định cứng: dispatch một event CDP cấp phát **0 byte** trên steady-state,
tính cả routing theo session. Trên máy phát triển, frame event 150 byte dispatch mất khoảng
234 ns, event có routing session khoảng 258 ns. Bộ test đầy đủ gồm **222 test**.

## Yêu cầu

- .NET SDK 10.0
- Đã cài sẵn một trình duyệt họ Chromium. ZeroCDP **không bao giờ tự tải browser**.
  Đặt `ZEROCDP_CHROME_PATH` để ép đường dò tìm.

## Bắt đầu nhanh

```csharp
using ZeroCdp.Browser;
using ZeroCdp.Protocol;

await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
{
    Headless = true,
    Stealth = StealthProfile.Default,
});

await using CdpPage page = await browser.Browser.NewPageAsync();
await page.GotoAsync("https://example.com");

string title = await page.TitleAsync();
string content = await page.ContentAsync();

await page.Locator("button.submit").ClickAsync();
await page.Locator("#field").TypeAsync("hello");

// JavaScript chạy trong isolated world. Không thấy global của trang, DOM thì dùng chung,
// và không bao giờ bật Runtime domain.
EvaluationResult result = await page.EvaluateAsync("document.querySelectorAll('a').length");
if (result.Value.TryGetInt64(out long links))
{
    Console.WriteLine($"{links} links");
}
```

Locator được resolve lại ở **mọi** action, nên vẫn hoạt động sau điều hướng hoặc re-render. Action
poll tối đa 30 giây (đổi được qua `CdpPage.DefaultTimeout` hoặc timeout từng locator) cho tới khi
phần tử được attach, visible và enabled; nhập văn bản còn yêu cầu field có thể edit.
Locator thường khi truy vấn nhanh của Chrome trả 0 kết quả sẽ tự fallback sang snapshot
`DOM.getDocument` với `pierce:true`, nên **closed shadow root** chạm được mà không cần cờ nào,
khớp theo Patchright. Fallback bị throttle 200 ms để selector không tồn tại không snapshot
cả document mỗi lần poll. Đường `pierceShadowDom: true` tường minh vẫn giữ nguyên:

```csharp
Locator inner = page.Locator("#inner", pierceShadowDom: true);
await inner.ClickAsync();
```

### API nâng cao kiểu Playwright

API gốc vẫn dùng được, và phần bề mặt nâng cao nhận luôn options object, enum và cách đặt tên kiểu
Playwright. Các overload này cộng thêm; code cũ không phải đổi.

```csharp
using ZeroCdp.Browser;

await page.GotoAsync("https://example.com", new PageGotoOptions
{
    WaitUntil = WaitUntilState.DOMContentLoaded,
    Timeout = TimeSpan.FromSeconds(15),
});

await page.GetByRole(AriaRole.Button, new RoleOptions { Name = "Sign in" }).ClickAsync(
    new LocatorClickOptions { Timeout = TimeSpan.FromSeconds(5) });

await page.Locator("#email").FillAsync(
    "ada@example.com",
    new LocatorFillOptions { Timeout = TimeSpan.FromSeconds(5) });

await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
await page.WaitForURLAsync("**/dashboard");
await page.Locator("#summary").WaitForAsync(new LocatorWaitForOptions
{
    State = WaitForSelectorState.Visible,
});

await page.ScreenshotAsync(new PageScreenshotOptions
{
    Path = "dashboard.png",
    FullPage = true,
    Type = ScreenshotType.Png,
});

FrameLocator frame = page.FrameLocator("iframe#checkout");
await frame.GetByRole(AriaRole.Button, new RoleOptions { Name = "Pay" }).ClickAsync();

// Assert kiểu web-first: tự retry tới khi điều kiện đúng hoặc hết timeout.
await page.Expect().ToHaveTitleAsync("Checkout");
await page.Locator("#save").Expect().ToBeVisibleAsync();
await page.Locator(".row").Expect().ToHaveCountAsync(3);
await page.Locator("#email").Expect().ToHaveValueAsync("ada@example.com");

// Handle giữ một object JS remote sống sót qua nhiều lần gọi.
await using JSHandle config = await page.EvaluateHandleAsync("window.appConfig");
await using ElementHandle save = await page.QuerySelectorAsync("#save");
Assert.NotNull(save);
await save!.ClickAsync();
string? state = await save.GetAttributeAsync("data-state");

// HTTP client độc lập với options kiểu Playwright; không cần page trình duyệt.
await using APIRequestContext api = await APIRequest.NewContextAsync(new APIRequestNewContextOptions
{
    BaseUrl = "https://api.example.test",
    ExtraHttpHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" },
    Timeout = TimeSpan.FromSeconds(10),
});
await using APIResponse apiResponse = await api.GetAsync("/users/1");
JsonElement? json = await apiResponse.JsonAsync();

// Hoặc dùng client request thuộc sở hữu context. Client chia sẻ cookie jar của context theo
// cả hai chiều: response của API cập nhật cookie trình duyệt, và request API gửi cookie sẵn có.
// page.APIRequest trả về cùng client với page.Context.APIRequest.
await using APIResponse contextResponse = await context.APIRequest.GetAsync("/account");
Assert.Same(context.APIRequest, page.APIRequest);
string account = await contextResponse.TextAsync();

// Coverage là opt-in, dùng domain Profiler/CSS mà không bật Debugger.enable.
await page.Coverage.StartJSCoverageAsync();
await page.Coverage.StartCSSCoverageAsync();
await page.GotoAsync("https://example.com");
IReadOnlyList<JSCoverageEntry> jsCoverage = await page.Coverage.StopJSCoverageAsync();
IReadOnlyList<CssCoverageEntry> cssCoverage = await page.Coverage.StopCSSCoverageAsync();
```

`WaitForURLAsync` là extension method vì một member chỉ khác chữ hoa/thường với gốc
`WaitForUrlAsync` sẽ vi phạm CA1708. Các type gốc `WaitUntil`, `WaitForState`, `ScreenshotOptions`
vẫn là phần implement; các type kiểu Playwright chỉ là adapter mỏng.

### Bề mặt action

Phần P0–P4 phủ các action mà script tự động hoá thường cần:

```csharp
await page.Locator("#name").FillAsync("Ada");
await page.Locator("#terms").CheckAsync();
await page.Locator("#plan").SelectOptionAsync("pro");
await page.Locator("#name").PressAsync("Enter");
await page.Locator("#details").WaitForAsync(WaitForState.Visible, TimeSpan.FromSeconds(5));

// Locator ngữ nghĩa kiểu Playwright (dùng snapshot pierce, closed root cũng thấy).
await page.GetByRole("button", "Sign in").ClickAsync();
await page.GetByLabel("Email address").FillAsync("ada@example.com");
await page.GetByPlaceholder("Search").FillAsync("cdp");
await page.GetByText("Welcome back").WaitForAsync(WaitForState.Visible);
await page.GetByTestId("row-1").ClickAsync();
await page.GetByAltText("Company logo").ClickAsync();
await page.GetByTitle("Close dialog").ClickAsync();

// Đánh chỉ mục và đọc nội dung kiểu Playwright.
await page.Locator(".row").First().ClickAsync();
await page.Locator(".row").Nth(2).ClickAsync();
string[] texts = await page.Locator(".row").AllTextContentsAsync();
string[] renderedRows = await page.Locator(".row").AllInnerTextsAsync();
string? href = await page.Locator("a.primary").GetAttributeAsync("href");
string value = await page.Locator("#name").InputValueAsync() ?? string.Empty;
string rendered = await page.Locator("#summary").InnerTextAsync();

// Kiểm tra box, focus, trạng thái.
BoundingBox? box = await page.Locator("#name").BoundingBoxAsync();
await page.Locator("#name").FocusAsync();
bool enabled = await page.Locator("#save").IsEnabledAsync();
bool editable = await page.Locator("#name").IsEditableAsync();
bool checkedNow = await page.Locator("#terms").IsCheckedAsync();
bool hidden = await page.Locator("#banner").IsHiddenAsync();

// Chỉnh sửa, evaluate, event, drag.
await page.Locator("#name").ClearAsync();
await page.Locator("#name").PressSequentiallyAsync("Ada", TimeSpan.FromMilliseconds(20));
await page.Locator("#name").SelectTextAsync();
await page.Locator("#name").BlurAsync();
await page.Locator("#terms").SetCheckedAsync(true);
await page.Locator("#emit").DispatchEventAsync("custom-event", bubbles: true, cancelable: false);
EvaluationResult boxText = await page.Locator("#summary").EvaluateAsync("(element) => element.textContent");
await page.Locator("#drag").DragToAsync(page.Locator("#drop"));
await page.WaitForFunctionAsync("document.body.dataset.ready === 'yes'");
Locator ready = await page.WaitForSelectorAsync("#save");

// Options chuột tin cậy, upload và screenshot riêng phần tử.
await page.Locator("#menu").ClickAsync(new ClickOptions { Button = MouseButton.Right });
await page.Locator("#upload").SetInputFilesAsync("/tmp/report.pdf");
byte[] cardPng = await page.Locator(".card").ScreenshotBytesAsync();

await page.ScreenshotAsync("page.png", new ScreenshotOptions { FullPage = true });

FrameLocator checkout = page.FrameLocator("iframe#checkout");
await checkout.Locator("button.pay").ClickAsync();
await checkout.GetByRole("button", "Apply coupon").ClickAsync();
await checkout.GetByLabel("Postal code").FillAsync("94107");

// Iframe cross-site là out-of-process. ZeroCDP tự auto-attach child target theo page,
// route phần DOM/JS qua session của target đó, và compose affine transform của iframe nên
// click và BoundingBoxAsync dùng toạ độ viewport của trang chính. OOPIF lồng nhau cũng OK.
FrameLocator nested = page.FrameLocator("iframe#outer").Frame("iframe#inner");
await nested.GetByRole("button", "Continue").ClickAsync();

// Popup, download, file chooser là page event hạng nhất.
CdpPage popup = await page.RunAndWaitForPopupAsync(
    () => page.Locator("#open-popup").ClickAsync());
Download download = await page.RunAndWaitForDownloadAsync(
    () => page.Locator("#export").ClickAsync());
await download.WaitForCompletionAsync();
await download.SaveAsAsync("/tmp/export.csv");

FileChooser chooser = await page.RunAndWaitForFileChooserAsync(
    () => page.Locator("#pick-file").ClickAsync());
await chooser.AcceptAsync("/tmp/report.pdf");

// Dialog tự dismiss khi KHÔNG có subscriber. Khi có subscriber, dialog giữ nguyên cho tới
// khi gọi AcceptDialogAsync/DismissDialogAsync.
page.AutoDismissDialogs = false;
page.DialogOpening += dialog => Console.WriteLine($"{dialog.Type}: {dialog.Message}");

// Gọi trong flow của bạn sau action mở dialog; trả false nếu không có dialog nào mở.
await page.AcceptDialogAsync();
```

### Context, storage, tracing, video

Isolated browser context cô lập cookie, cache và permission. Options của context được áp dụng trước
navigation đầu tiên. `context.APIRequest` (cũng được expose là `page.APIRequest`) chia sẻ cookie jar
của context theo cả hai chiều: response API cập nhật cookie trình duyệt, request API gửi cookie
trình duyệt sẵn có.

```csharp
await using BrowserContext context = await browser.Browser.NewContextAsync(new BrowserContextOptions
{
    Viewport = new ViewportSize(1280, 800),
    Locale = "en-US",
    TimezoneId = "America/Chicago",
    ColorScheme = PreferredColorScheme.Dark,
    Geolocation = new Geolocation(40.7128, -74.0060),
});

await context.GrantPermissionsAsync("geolocation", "https://example.com");
await using CdpPage isolated = await context.NewPageAsync("https://example.com");

CookiesResult cookies = await context.GetCookiesAsync();
IReadOnlyList<CdpCookie> cookieList = await context.CookiesAsync();
await context.AddCookiesAsync([new CdpCookie { Name = "sid", Value = "1", Url = "https://example.com/" }]);
await context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = "sid" });
await context.ClearCookiesAsync();
await context.ClearGeolocationAsync();
await context.UnrouteAllAsync(); // route HTTP và WebSocket

// Storage state tương thích Playwright, gồm cả localStorage cho origin HTTP(S).
StorageState state = await context.StorageStateAsync();
await state.SaveAsync("/tmp/state.json");
StorageState loaded = await StorageState.LoadAsync("/tmp/state.json");
await otherContext.SetStorageStateAsync(loaded);

// IndexedDB là opt-in, dùng wire shape value/key + valueEncoded/keyEncoded của Playwright.
StorageState fullState = await context.StorageStateAsync(
    new StorageStateOptions { IndexedDb = true });

// ARIA/accessibility snapshot, đồng hồ giả, HAR replay.
string aria = await page.Locator("main").AriaSnapshotAsync();
await page.Clock.InstallAsync();
await context.RouteFromHARAsync("/tmp/session.har");

// Ghi trace kèm video theo page. Encode video cần ffmpeg; FfmpegPath có thể trỏ tới
// file ffmpeg kèm theo ứng dụng.
await using BrowserContext recorded = await browser.Browser.NewContextAsync(new BrowserContextOptions
{
    RecordVideo = new RecordVideoOptions { Dir = "/tmp/videos" },
});
await recorded.Tracing.StartAsync(new TracingStartOptions
{
    Title = "checkout",
    Screenshots = true, // frame screencast liên tục
    Snapshots = true,   // snapshot MHTML lúc load và biên chunk
    Sources = true,     // resource JS, CSS và source map
});
CdpPage videoPage = await recorded.NewPageAsync("https://example.com");
await recorded.Tracing.StopChunkAsync("/tmp/checkout-1.zip");
await videoPage.GotoAsync("https://example.com/next");
await recorded.Tracing.StopAsync("/tmp/checkout-2.zip");
await videoPage.CloseAsync();
string videoPath = await videoPage.Video!.PathAsync();

// Đọc archive đã ghi không cần browser và sinh viewer tự chứa.
using TraceArchive trace = TraceArchive.Open("/tmp/checkout-2.zip");
TraceContextOptions? contextInfo = trace.Context;
IReadOnlyList<TraceAction> actions = trace.Actions;
IReadOnlyList<TraceNetworkEntry> requests = trace.Network;
byte[] firstFrame = trace.ReadEntryBytes(trace.ScreencastFrames[0].File);
await trace.WriteViewerAsync("/tmp/checkout-viewer.html");
```

### Can thiệp network

Interception network chỉ bật `Fetch.enable` trong khi **tồn tại** ít nhất một route. Observation và
waiter bật `Network.enable`; các đường đó không bật `Runtime.enable` hay `Console.enable`.
`RouteWebSocketAsync` là opt-in riêng, bật Runtime cho transport console của nó:

```csharp
await page.RouteAsync("**/api/users", async route =>
{
    await route.FulfillAsync(
        200,
        """[{"id":1,"name":"Ada"}]""",
        "application/json; charset=utf-8");
});

// Context route bao phủ page có sẵn và page tạo sau, kể cả navigation đầu tiên.
// Route của page ưu tiên hơn route của context.
await context.RouteAsync("**/api/**", async route =>
{
    await route.FulfillAsync(200, """{"cached":true}""", "application/json");
});

// Tiếp tục ra network với URL/method/body/header viết lại. Host gốc và header khác được merge;
// header cũ về độ dài body bị bỏ để Chromium tiếp tục đúng.
await page.RouteAsync("**/legacy/**", route => route.ContinueAsync(
    "https://example.test/v2/items",
    method: "POST",
    postData: """{"migrated":true}""",
    headers: [new CdpHeader("content-type", "application/json")]));

// Route WebSocket là opt-in. Handler khớp là một mock cục bộ; gọi ConnectToServerAsync để
// proxy sang server thật. Khi page có route, wrapper phủ mọi socket của page đó; socket không
// khớp bị proxy trong suốt. Không có đăng ký nào thì không inject gì cả.
await page.RouteWebSocketAsync("ws://example.test/**", async socket =>
{
    socket.OnMessage(message => Console.WriteLine($"client: {message}"));
    socket.OnServerMessage(message => Console.WriteLine($"server: {message}"));
    await socket.ConnectToServerAsync();
});

Task<PageResponse> wait = page.WaitForResponseAsync("**/api/**");
Task<PageResponse> contextWait = context.WaitForResponseAsync("**/api/**");
Task<PageResponse> finishedWait = page.WaitForRequestFinishedAsync("**/api/**");
Task<RequestFailure> failedWait = context.WaitForRequestFailedAsync("**/offline/**");
await page.EvaluateAsync("fetch('/api/users')");
PageResponse response = await wait;
PageResponse sameResponse = await contextWait;
PageResponse finished = await finishedWait;
NetworkBody body = await page.GetResponseBodyAsync(response);

context.Request += request => Console.WriteLine($"{request.Method} {request.Url}");
context.Response += observed => Console.WriteLine($"{observed.Status} {observed.Url}");
context.RequestFinished += done => Console.WriteLine($"{done.Status} {done.Url}");
context.RequestFailed += failure => Console.WriteLine($"{failure.ErrorText} {failure.Url}");
await context.EnableNetworkAsync();

// Waiter có thể bọc luôn thao tác gây ra sự kiện.
PageResponse fetched = await page.RunAndWaitForRequestFinishedAsync(
    () => page.EvaluateAsync("fetch('/api/users')"));
string commit = await page.RunAndWaitForNavigationAsync(
    () => page.Locator("#next").ClickAsync(),
    waitUntil: WaitUntil.Commit);

await page.ReloadAsync();
await page.GoBackAsync();
await page.WaitForUrlAsync("**/dashboard");
await page.WaitForLoadStateAsync(WaitUntil.Load);
```

Helper điều hướng gồm `ReloadAsync`, `GoBackAsync`, `GoForwardAsync`, `GetNavigationHistoryAsync`,
`WaitForUrlAsync`, `WaitForNavigationAsync`, `WaitForLoadStateAsync`, `SetContentAsync`,
`SetViewportSizeAsync` cùng property `ViewportSize`, `EmulateColorSchemeAsync`, `PdfAsync`,
`BringToFrontAsync`, `SetBypassCspAsync`, `SetGeolocationAsync`, các chế độ offline/cache-header,
và đọc response body. `RunAndWaitForRequestAsync`, `RunAndWaitForResponseAsync`,
`RunAndWaitForRequestFinishedAsync`, `RunAndWaitForRequestFailedAsync` và
`RunAndWaitForWebSocketAsync` theo đúng cách đặt tên của Playwright ở cả page lẫn context. Bề mặt
page, frame và handle cũng có các helper đọc/trạng thái selector (`GetAttributeAsync`,
`InnerHTMLAsync`, `InnerTextAsync`, `TextContentAsync`, `InputValueAsync`, `IsVisibleAsync`,
`IsHiddenAsync`, `IsCheckedAsync`, `IsEnabledAsync`, `IsDisabledAsync`, `IsEditableAsync`) và
các action của handle (`TapAsync`, `SelectTextAsync`, `DispatchEventAsync`,
`CheckAsync`/`UncheckAsync`/`SetCheckedAsync` kèm options). API response có `HeadersAsync` và
`HeadersArrayAsync`, còn `APIRequestContext.StorageStateAsync` chụp lại cookie.

## Mô hình stealth

ZeroCDP tuân theo một quy tắc duy nhất: *không thêm vào trang bất cứ thứ gì mà trang nhìn thấy được*.

| Điểm rò | Hành vi của ZeroCDP |
|---|---|
| `Runtime.enable` | Không gửi trong flow page/stealth thông thường. Evaluate dùng `Page.createIsolatedWorld` + `executionContextId` tường minh, kể cả trong frame. `Runtime.evaluate` main-world là opt-in tường minh và cũng không bao giờ bật domain. `RouteWebSocketAsync` là ngoại lệ opt-in duy nhất: nó bật Runtime để nhận transport console của wrapper. |
| `Console.enable` | Không bao giờ gửi. |
| `Target.setDiscoverTargets` | Không bao giờ gửi. Chỉ dùng flattened `Target.setAutoAttach` ở session browser và page (session page là bắt buộc cho OOPIF). |
| Script inject | None mặc định: không `Page.addScriptToEvaluateOnNewDocument`, không `sourceURL`. `AddInitScriptAsync` dùng transform response Fetch; `RouteWebSocketAsync` inject wrapper WebSocket. Cả hai là opt-in tường minh. |
| Domain network | `Network.enable` chỉ gửi khi cần observation hoặc waiter tường minh. `Fetch.enable` chỉ tồn tại khi có ít nhất một route được đăng ký. Route tự continue hoặc abort thay vì treo request paused. |
| Cờ automation | Không bao giờ truyền `--enable-automation`. `StealthProfile.Default` thêm `--disable-blink-features=AutomationControlled` và bỏ `--disable-gpu`. |
| Tên world | 32 ký tự hex ngẫu nhiên, duy nhất mỗi page, không mang danh tính thư viện. |
| Closed shadow root | Locator thường chạy truy vấn nhanh của Chrome trước, rồi fallback snapshot `DOM.getDocument` pierce có throttle, khớp theo Patchright mà không inject JavaScript. |

Giá trị `StealthProfile`:

- `Raw` — baseline trung tính; hữu ích để so sánh. `navigator.webdriver` có thể là true.
- `Minimal` — tắt `AutomationControlled`, giữ các cờ headless thông dụng.
- `Default` — `Minimal` cộng thêm việc bỏ fingerprint `--disable-gpu` của headless. Đây là mặc định.

Evaluate mặc định là isolated. `page.EvaluateAsync("window.app", isolatedContext: false)` chạy ở
main world của trang cho các trường hợp thật sự cần global của trang; đây là lối thoát kiểu
Patchright, không phải mặc định. Khi `Network.enable` được dùng cho routing/observation, thư viện
không bao giờ bật `Runtime.enable` hay `Console.enable` như tác dụng phụ. Ngoại lệ duy nhất là
đăng ký `RouteWebSocketAsync` tường minh — nó chọn bật `Runtime.enable` và một wrapper main-world;
CSP nghiêm ngặt có thể chặn wrapper đó.

Repo có trace harness ghi **mọi** lệnh gửi đi và khẳng định các lệnh
`Runtime.enable`, `Console.enable`, `Debugger.enable`, `Target.setDiscoverTargets` và lệnh inject
script không bao giờ xuất hiện trong flow page/network/stealth thông thường. Test cho đường
opt-in `RouteWebSocketAsync` **kỳ vọng** có `Runtime.enable`; không test nào coi lệnh đó là
stealth-safe. Bài check network-gate với bot.sannysoft.com chạy bằng `ZEROCDP_E2E_NETWORK=1`.

Stealth là một cuộc đua giáp bên trên không - cuộc đua tên lửa giữa thư viện và detector, không phải lời hứa. ZeroCDP nhắm tới hành vi
browser trông giống sử dụng thật; nó không hứa thắng mọi detector.

## Bảo mật

- **Không port debugger** mặc định trên mọi nền tảng hỗ trợ. Linux/macOS dùng fd 3/4 của tiến trình
  con; Windows dùng inherited anonymous-pipe handle truyền qua `--remote-debugging-io-pipes`.
  Không có socket lắng nghe nào ở chế độ pipe. Chế độ port chỉ bind `127.0.0.1`.
- **Private profile** cho mỗi lần launch, permission chỉ chủ sở hữu trên Unix, xóa khi dispose
  trừ khi `KeepProfile` hoặc `UserDataDirectory` tường minh được đặt.
- **Sandbox bật** mặc định. `DisableSandbox = true` là tường minh và phát trace warning.
- **Giới hạn tài nguyên**: `MaxFrameBytes` (32 MB), `MaxPendingCommands` (4096), `MaxDomDepth` (512)
  và `CommandTimeout` dùng timer wheel chung (30 s) không cấp phát cho từng lệnh. Screenshot
  full-page bị cap 16.384 px mỗi chiều và 64 triệu pixel.
- **`LogRedactor`** che mật khẩu, cookie, header authorization, token, `Input.insertText.value` và
  `Page.handleJavaScriptDialog.promptText` trước khi JSON được log. Input dị dạng bị thay thế,
  không log thô.
- **`NavigationPolicy`** là allow-list origin được kiểm trước `Page.navigate`:
  ```csharp
  await using var browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
  {
      NavigationPolicy = new NavigationPolicy("https://example.com", "https://api.example.com"),
  });
  ```
- Không bao giờ bật bypass lỗi certificate.

## Kiến trúc

```
src/ZeroCdp/
├─ Transport/   pipe (fd 3/4 trên POSIX, inherited handle trên Windows), WebSocket loopback
├─ Runtime/     connection, dispatcher, pending table, routing theo session
├─ Protocol/    writer lệnh và reader kết quả UTF-8 viết tay
├─ Browser/     launcher, browser, context, page/locator partials, frame,
│               options/enums, screenshot, dialog, input, network state/routing,
│               download, file chooser, storage state, tracing, trace reader/viewer,
│               API request, coverage, handle, WebSocket, video
├─ Stealth/     runtime isolated world, factory tên world
├─ Security/    navigation policy, log redaction
├─ Dom/         cây DOM snapshot cấp phát nhẹ
└─ Selectors/   parser và matcher CSS cho truy vấn pierce shadow
```

Không reflection, không serializer, không `dynamic`, không `JsonDocument` trên hot path. Result
struct parse thẳng từ receive buffer; span chỉ hợp lệ trong thời gian chạy callback, giữ giá trị
đòi hỏi copy tường minh.

## Build và test

```bash
dotnet build ZeroCDP.slnx -c Release
dotnet test tests/ZeroCdp.Tests/ZeroCdp.Tests.csproj -c Release
dotnet run --project tests/ZeroCdp.Benchmarks -c Release
dotnet run --project samples/ZeroCdp.Sample.RealWorld -c Release
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release
dotnet publish samples/ZeroCdp.Sample.AotConsole -c Release -r linux-x64
```

Sample real-world tự bật HTTP server trên port ngẫu nhiên, dùng browser profile riêng, và chạy
song song với test suite không xung đột.

`ZeroCdp.Sample.BotCheck` mở browser **headful** với persistent profile và giữ CDP session attach
khi bạn tự điều hướng. Trang đầu liên kết tới bot.sannysoft.com, rebrowser-bot-detector, CreepJS,
BrowserScan và Pixelscan. Đóng cửa sổ trình duyệt hoặc bấm Ctrl+C để kết thúc. Các option hữu ích:

```bash
# Mở thẳng trang cụ thể.
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release -- \
  --start https://bot.sannysoft.com/

# Dùng persistent profile hoặc launch profile khác.
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release -- \
  --user-data-dir /tmp/zerocdp-botcheck --stealth default
```

Profile mặc định là `~/.zerocdp/botcheck-profile`, nên cookie và storage của site giữ lại giữa
các lần chạy. Cờ `--headless` tùy chọn chỉ phục vụ smoke test; chế độ thường là cửa sổ nhìn thấy
để tự kiểm tra.

### Đóng gói

```bash
dotnet pack src/ZeroCdp/ZeroCdp.csproj -c Release
```

Gói được tạo dưới `artifacts/`, chứa README bên cạnh binary của thư viện.

### Benchmark

Số đo Release đại diện trên linux-x64: dispatch **~234 ns** cho event 150 byte và **0 B/op** trên
2.000.000 lần dispatch; dispatch có routing session **~258 ns** và **0 B/op**; evaluate isolated
**~339 µs**; truy vấn selector **~805 µs**; tra cứu closed-shadow **~573 µs**; tra cứu
closed-shadow tự động **~650 µs**; tra cứu role ngữ nghĩa **~550 µs**; click locator **~33 ms**;
`Browser.getVersion` **~133 µs**. Sample AOT publish ra **8,0 MiB**, gói NuGet khoảng **450 KB**.

Test suite chịu được máy không có browser: test phụ thuộc môi trường trả sớm thay vì fail. Trên
máy có Chromium, bộ **222 test** chạy thật pipe transport, isolated world, context, page/context
route và observation, response body, lịch sử điều hướng, locator CSS và ngữ nghĩa, frame
cùng tiến trình và out-of-process (kể cả OOPIF lồng nhau), pierce closed-shadow, storage state
(kể cả IndexedDB), popup, download, file chooser, upload, permission/geolocation, screenshot,
dialog, assertion, handle, API request (kể cả chia sẻ cookie context), coverage, HTTP auth,
route WebSocket, trace archive/viewer, video và security policy.

## Giới hạn

- Chỉ hỗ trợ họ Chromium. Firefox/WebKit cần WebDriver BiDi.
- Pipe mode Windows dựa vào `Process.Start` kế thừa hai anonymous-pipe handle. Nếu host process
  áp đặt giới hạn kế thừa handle lạ, hãy dùng `CdpTransportKind.Port` cho deployment đó. Đường
  pipe Windows không được chạy kiểm trên máy Linux phát triển này.
- `NavigationPolicy` chỉ kiểm navigation khởi đầu. Redirect phía server cần interception tường
  minh; route handler không tự bị ràng buộc bởi policy.
- `FrameLocator` hỗ trợ iframe cùng tiến trình và out-of-process, kể cả OOPIF lồng nhau,
  closed shadow root và locator ngữ nghĩa `GetBy*`. Child session được auto-attach theo page;
  lệnh DOM/JS được route về target sở hữu, còn click và bounding box map ngược về viewport
  trang chính. Frame trong page có CSS transform đặc biệt được map bằng affine transform của
  content box iframe.
- Actionability kiểm trạng thái attached, visible, enabled và (với nhập văn bản) editable, cộng
  hit-target/occlusion và stable-box hai frame. Hit-target kiểm chấp nhận host khi target nằm
  trong closed shadow root, vì không resolve được hit node qua ranh giới đó nếu không inject script.
- Locator ngữ nghĩa dùng snapshot pierce-DOM managed và phép tính accessible-name xấp xỉ cho các
  role native HTML phổ biến. Quan hệ ARIA phức tạp và custom role provider chưa đầy đủ.
- Xuất storage state liệt kê localStorage cho origin HTTP(S) với một page mở trong context.
  IndexedDB là opt-in qua `StorageStateOptions.IndexedDb`, giữ database, store, index và record
  bằng JSON của Playwright cộng wire shape encoded cho Date/typed array/Map/Set. Import gieo
  origin qua target tạm, không chạm origin thật.
- Document đầu tiên của popup có thể load trước khi target session được đăng ký, nên context
  route có thể chỉ bắt đầu intercept từ các request sau đó. Page tạo qua `NewPageAsync` được
  route trước navigation đầu tiên.
- Download dùng chung một thư mục tạm mỗi browser, đặt tên theo download GUID của Chromium.
  `SaveAsAsync` copy file đã hoàn thành; file gốc bị xóa khi context/browser dọn dẹp hoặc khi
  gọi `DeleteAsync`.
- `DragToAsync` thực hiện chuột trái tin cậy với các bước move trung gian. Custom drag interception
  và drag-drop file ngoài chưa có.
- Frame WebSocket được quan sát qua domain Network, và `RouteWebSocketAsync` mock hoặc proxy
  được. Routing cài wrapper main-world `WebSocket` và bật `Runtime.enable`; điều này vốn nhìn
  thấy được từ phía trang và CSP nghiêm ngặt có thể chặn. Khi page có route, mọi socket của page
  đó đi qua wrapper; socket không khớp bị proxy trong suốt. Không đăng ký nào thì không wrapper.
  Thương lượng permessage-deflate là việc của browser. Route HTTP dùng `Fetch.enable`; observation
  chỉ bật `Network.enable` khi được yêu cầu.
- Screenshot full-page bị cap 16.384 px mỗi chiều và tổng 64 triệu pixel. Screenshot phần tử bỏ
  qua `FullPage` và chụp bounding box nhìn thấy của phần tử.
- Ghi video cần `ffmpeg` trên `PATH`, trong `ZEROCDP_FFMPEG_PATH`/`FFMPEG_PATH`, hoặc tại
  `RecordVideoOptions.FfmpegPath`; WebM của từng page được finalize khi page đó đóng.
- Tracing dùng chung một `Page.startScreencast` mỗi page nên video và tracing có thể chạy cùng
  nhau chỉ với một acknowledgement frame. Frame lấy mẫu ~5 fps, ghi ra file tạm trước khi stream
  vào zip để giữ bộ nhớ bounded. Mỗi `StopChunkAsync` đóng gói event của chunk đó và bắt đầu
  chunk mới; `trace.network` là tích lũy cho phiên ghi.
- `Snapshots` lưu MHTML tự chứa tại nút load và biên chunk/stop. Đây là snapshot DOM thực dụng,
  không phải schema layout/ARIA snapshot của Playwright. `Sources` gói body JS, CSS và `.map`,
  theo `sourceMappingURL`, cap mỗi resource 4 MiB. `TraceArchive` đọc archive ZIP hoặc thư mục
  trích sẵn, expose action/network/frames/sources có type, và viết viewer HTML tự chứa. UI
  trace-viewer React của Playwright, codegen và test runner nằm ngoài phạm vi.
- `ConsoleMessage`, `PageError`, `WebError` và các console event không được expose như page event
  thường, vì chúng cần `Runtime.enable`/`Console.enable`; cần thì tự bật `Log.enable` tường minh.
  API pause debugger cũng nằm ngoài bề mặt tương thích Patchright.
- Test runner, codegen và UI mode của Playwright nằm ngoài phạm vi thư viện một cách chủ đích.

## Giấy phép

MIT. Xem [LICENSE](LICENSE).

Bản tiếng Anh đầy đủ của tài liệu này là [README.md](README.md).
