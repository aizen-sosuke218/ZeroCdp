# ZeroCDP — Kế hoạch kiến trúc & triển khai

Thư viện CDP cho .NET 10: cross-platform, NativeAOT-compatible, zero-alloc trên hot path,
stealth theo chuẩn Patchright (tự implement lại bằng native code, không port code Python),
và security-first.

---

## 0. Trạng thái triển khai

| Hạng mục | Trạng thái | Ghi chú |
|---|---|---|
| M0 skeleton + AOT publish | ✅ | `dotnet publish -r linux-x64` ra binary **6.5 MiB**, chạy được, không reflection |
| M1 transport + dispatch | ✅ | Pipe fd3/fd4 trên POSIX, inherited handle + `--remote-debugging-io-pipes` trên Windows; **pipe là mặc định**, port-mode cho attach/opt-in |
| M2 session/target | ✅ | Nhiều page trên 1 connection, demux `sessionId` 0 alloc, auto-attach + resume target |
| M3 page core | ✅ | `GotoAsync` (Commit/DOMContentLoaded/Load), title/url/content, cookie, close |
| M4 locator + input | ✅ | CSS engine tự viết, click/type/scroll, **closed shadow root** |
| M5 stealth | ✅ | Isolated world (không `Runtime.enable`), world name ngẫu nhiên, 3 launch profile, trace harness |
| M6 security | ✅ | `LogRedactor`, cap frame/pending/DOM depth, command timeout dùng timer wheel chung, `NavigationPolicy` |
| M7 package + docs | ✅ | Benchmark, `dotnet pack`, README, sample AOT 6.5 MiB |
| P0 Playwright surface | ✅ | Screenshot, dialog, iframe same-process, auto-wait/actionability, Fill/Check/Hover/Select/Press; 123 test |
| P1 Playwright surface + Patchright | ✅ | Context isolation, network route/observe, navigation history, semantic locators, locator indexing/read/state/box/focus/upload/screenshot, permissions/geolocation, closed-shadow auto fallback, main-world opt-in; **147 test** |
| P2 Playwright surface mở rộng | ✅ | Popup/download/file chooser, storage state JSON, context route/event, frame closed-shadow + semantic, locator edit/eval/drag, wait helper; **156 test** → hiện **158** |
| P3 selector parity + Patchright parity | ✅ | XPath (kể cả closed shadow), text/id/data-testid/role engine, Filter/And/AllAsync, RoleOptions, hit-target + stable box, networkidle, keyboard/mouse, route fallback, context network helpers, ExposeFunction/ExposeBinding, AddInitScript kiểu Patchright, connectOverCDP; **179 test** |
| P4 OOPIF + media/session mở rộng | ✅ | OOPIF cross-site và lồng nhau: page-level auto-attach, child-session routing, synthetic node id, affine transform; Playwright options/enums; sample thực tế chạy song song test suite; **181 test** |
| P5 Playwright/Patchright parity | 🚧 | Checklist chi tiết ở §0.1. Đã xong Frame API lõi, strict locator mode, context defaults + AddInitScript, Page lifecycle/network events, Request/Response helpers, locator handlers, touch input, click options, per-page emulation, workers, highlight, security details, hover/tap/fill options, HAR recording/replay, accessibility/ARIA snapshot, Clock, storage state + IndexedDB, tracing recording, video, public CDP sessions, service workers; **211 test** (P6 bổ sung 11) |
| P6 Gap-list parity | ✅ | Web-first assertions, JS/Element handles, standalone và context-bound `APIRequestContext`, coverage, option/context parity, HTTP auth, `routeWebSocket`, trace archive reader/viewer; **222 test** |

### 0.1 Checklist Playwright/Patchright parity

Đánh dấu `[x]` khi đã có API + test Chromium; `[~]` khi có API nhưng còn thiếu hành vi/option.
Các mục Patchright không dùng hoặc không hỗ trợ được đánh dấu `[skip]` và không triển khai.

#### P0 — nền tảng API và hành vi locator

- [x] `Frame` công khai: `Page`, `FrameId`, `ParentFrame`, `ChildFrames`, `Name`, `Url`, `IsDetached`.
- [x] `Page.MainFrame`, `Page.Frames`, `Page.Frame(nameOrUrl)`.
- [x] Frame tree từ `Page.getFrameTree` + `Page.frameAttached/frameDetached/frameNavigated`.
- [x] `Frame.Locator`, toàn bộ `GetBy*`, `FrameLocator`, `EvaluateAsync`, `ContentAsync`, `TitleAsync`.
- [x] `Frame.WaitForURLAsync`, `WaitForLoadStateAsync`, `WaitForSelectorAsync`, `WaitForFunctionAsync`, `WaitForTimeoutAsync`.
- [x] `Frame.GotoAsync`, `Frame.SetContentAsync` (same-process và OOPIF).
- [x] `Page.FrameAttached`, `Page.FrameDetached`, `Page.FrameNavigated`.
- [x] Strict locator mode mặc định bật; `CdpStrictModeException`; `Page.StrictSelectors`.
- [x] `BrowserContext.SetDefaultTimeout`, `SetDefaultNavigationTimeout`, `AddInitScriptAsync`; áp dụng cho page mới và page đang có.
- [x] Page events: `Load`, `DOMContentLoaded`, `Close`, `Request`, `Response`, `RequestFailed`, `DownloadOpening`, `FileChooserOpening`.
- [skip] `Page.Crash`: Chromium chỉ phát `Target.targetCrashed` khi bật `Target.setDiscoverTargets` (bị cấm); `Inspector.targetCrashed` cũng không phát trong thực nghiệm. Ghi nhận là giới hạn, không thêm lệnh stealth.
- [x] `Page.WaitForLoadStateAsync`, `Page.WaitForURLAsync`, `Page.ReloadAsync`, `GoBackAsync`, `GoForwardAsync` overload parity.
- [~] `Request`/`Response`: đã có `HeadersAsync`, `AllHeadersAsync`, `PostDataBufferAsync`, `ResponseAsync`, `FinishedAsync`, `Request`, `Protocol`, `Timing`, `EncodedDataLength`, `Failure`, `RedirectedFrom/To`, `RedirectResponseStatus`, `SizesAsync` (xấp xỉ), `ServerAddrAsync`, `SecurityDetails` (test HTTPS local); còn kích thước request chính xác (CDP không cung cấp đủ).
- [x] Locator: `IsDisabledAsync`, `Or`, `EvaluateAllAsync`, `PressSequentiallyAsync`, `ClearAsync`, `SelectTextAsync`, `SetCheckedAsync`, `DispatchEventAsync`, `ScrollIntoViewIfNeededAsync`.
- [x] `Locator.TapAsync` (options `Force`/`Trial`/`Position`/`Modifiers`/`NoWaitAfter`), `Page.Touchscreen.TapAsync` (trusted `Input.dispatchTouchEvent`).
- [x] `Locator.HighlightAsync` (dùng `Overlay.enable`/`Overlay.highlightNode`, chỉ bật khi gọi).
- [x] `Locator.ClickAsync` options `Force`, `Trial`, `Position`, `Button`, `ClickCount`, `Delay`, `Modifiers`, `Timeout`.
- [~] `NoWaitAfter` đã có trên `Fill`/`Press`/`Type`/`Hover`/`Tap`/`SelectOption`/`SetInputFiles`; `Fill.Force` chạy được cả input ẩn; còn options cho `Check`/`Uncheck`/`DragTo` và parity option đầy đủ của các action khác.
- [x] `Page` legacy wrappers: `ClickAsync`, `FillAsync`, `TypeAsync`, `PressAsync`, `SelectOptionAsync`, `SetInputFilesAsync`, `DragAndDropAsync`, `DispatchEventAsync`.
- [x] `Page.AddLocatorHandlerAsync`, `RemoveLocatorHandler`, `RemoveLocatorHandlerAsync`.
- [x] `Browser.Version`, `Browser.IsConnected`, `Browser.Contexts`, `Context.CloseAsync`, `Page.OpenerAsync`.
- [x] `BrowserContextOptions.StrictSelectors` áp dụng cho page mới.
- [x] `Browser.NewPageAsync(BrowserNewPageOptions)`; `Context.SetGeolocationAsync`, `Context.ClearPermissionsAsync`, `Context.GrantPermissionsAsync`.
- [ ] `Page.PauseAsync` — `[skip]` theo Patchright (debugger/pause không dùng).
- [ ] `ConsoleMessage`, `PageError`, `WebError`, console events — `[skip]` theo Patchright.
- [x] `page.routeWebSocket` / `WebSocketRoute` — opt-in, mock/proxy frame, binary, close và context route; dùng `Runtime.enable` + wrapper main-world (ngoại lệ có chủ đích).
- [~] Playwright Test runner, codegen, UI mode — `[skip]` (ngoài phạm vi thư viện). Trace archive có reader/schema/viewer self-contained; chưa phải React trace viewer của Playwright.

#### P1 — network, storage, media, worker

- [x] HAR recording qua `BrowserContextOptions.RecordHarPath` (HAR 1.2, response body embed/base64; test Chromium) và HAR replay qua `BrowserContext.RouteFromHARAsync`/`Page.RouteFromHARAsync` (`HarReplayOptions`, `HarNotFoundAction.Abort`/`Fallback`).
- [x] Tracing recording qua `BrowserContext.Tracing.StartAsync`/`StopAsync`, archive gồm `trace.trace`, `trace.network`, screencast frame liên tục, MHTML snapshot và source/source-map; chunk thật qua `StopChunkAsync`, hỗ trợ page thêm trước/sau khi bật trace.
- [x] Video recording qua `RecordVideoOptions` + `Page.Video` (`PathAsync`/`SaveAsAsync`/`DeleteAsync`), CDP `Page.startScreencast` dùng chung `ScreencastHub` → ffmpeg WebM; có thể chỉ định `FfmpegPath`.
- [x] Clock API (`Page.Clock.Install/FastForward/RunFor/PauseAt/Resume/SetFixedTime/SetSystemTime`), fake timer JS inject qua document transformer, giữ được qua navigation, không dùng init-script CDP bị cấm.
- [x] `BrowserContext.StorageStateAsync` đầy đủ định dạng Playwright; `StorageStateOptions.IndexedDb` xuất/nhập database, object store, index và record (JSON + encoded Date/typed array/Map/Set).
- [~] `Worker`/`ServiceWorker`: dedicated worker qua `Page.Worker`; service worker qua `BrowserContext.ServiceWorker(s)` + `EvaluateAsync` đã có test; shared worker không thấy target attach trong Chromium 155 headless nên chưa có test.
- [x] `Page.RequestGCAsync`, `Page.SetWebLifecycleStateAsync`, `Page.SetJavaScriptEnabledAsync`.
- [x] Route nâng cao: URL/method/headers và `FulfillAsync`/`AbortAsync` đã có test; `postData` dùng base64 đúng chuẩn. Khi thay body, header gốc được merge và bỏ `Content-Length`/`Transfer-Encoding` cũ để Chromium 155 không treo; đã có e2e cho cả trường hợp có và không có header override.
- [x] Download `PathAsync`/`SaveAsAsync`/`DeleteAsync`/`FailureAsync`.
- [x] File chooser `SetFilesAsync` với buffer/payload (test Chromium).
- [ ] Permission/geolocation/media/emulation option parity.

#### P2 — bề mặt mở rộng

- [x] Accessibility snapshot qua `Page.Accessibility.SnapshotAsync(...)` (`Accessibility.getFullAXTree`, forest cho ignored root, YAML serializer) và `Locator.AriaSnapshotAsync(...)`.
- [~] `GetByRole` accessible-name chính xác hơn: snapshot AX đã có, nhưng role locator vẫn dùng heuristic native HTML/ARIA rút gọn.
- [x] `Locator.HighlightAsync`.
- [x] `Page.Accessibility` snapshot.
- [ ] `BrowserType`/`Browser.NewContext` option parity còn lại.
- [x] `CDPSession` công khai (`BrowserContext.NewCDPSessionAsync`), session độc lập, `DetachAsync` không đóng page; test Chromium.
- [x] `Page.Clock` (`Install/FastForward/RunFor/PauseAt/Resume/SetFixedTime/SetSystemTime`), fake timer JS qua document transformer.
- [x] `Page.Video` (`PathAsync`/`SaveAsAsync`/`DeleteAsync`) qua `RecordVideoOptions`.
- [x] `Page.Workers`, `Page.Worker` event và `Worker.EvaluateAsync` (dedicated worker).
- [x] `Frame` legacy action methods (`ClickAsync`, `FillAsync`, `PressAsync`, ...).
- [x] `LocatorAssertions`/`PageAssertions` cho migration từ Playwright, gồm retry, phủ định và screenshot assertion.

**Gate P5**: mỗi mục `[x]` phải có test Chromium chạy thật; không thêm lệnh stealth bị cấm
(`Runtime.enable`, `Console.enable`, `Debugger.enable`, `Target.setDiscoverTargets`,
`Page.addScriptToEvaluateOnNewDocument`). T0 dispatch vẫn phải giữ **0 B/op**.

**Số đo trên máy này** (linux-x64, Release, `dotnet run --project tests/ZeroCdp.Benchmarks`):

| Phép đo | Kết quả |
|---|---|
| dispatch 1 event frame (150 B) | **~234 ns**, **0 B/op** |
| dispatch event có `sessionId` (qua router) | **~258 ns**, **0 B/op** |
| parse một string nhỏ | ~35 ns |
| `Locator.CountAsync` (đường `querySelectorAll`) | ~805 µs |
| `Locator.CountAsync` (pierce shadow, snapshot) | ~573 µs |
| `Locator.CountAsync` (auto fallback closed shadow) | ~650 µs |
| `Locator.CountAsync` (`GetByRole`, snapshot + matcher) | ~550 µs |
| `Locator.ClickAsync` (resolve + scroll + hover/press/release) | ~33 ms |
| `Page.EvaluateAsync` (isolated world, context tái sử dụng) | ~339 µs |
| `Browser.getVersion` round-trip qua pipe | **~133 µs** (phần lớn là Chrome xử lý) |
| publish NativeAOT | 8.0 MiB, launch + CDP OK |
| `dotnet pack` | 429 KB `.nupkg`, chỉ BCL |

**Sai lệch so với kế hoạch ban đầu**

- Pipe transport đã xong ngay ở M1 cho Linux/macOS, không cần chờ v2: `posix_spawnp` +
  `posix_spawn_file_actions_adddup2` hoạt động đúng. Windows dùng hai `AnonymousPipeServerStream`
  inheritable + `--remote-debugging-io-pipes` (handle thập phân), cùng mô hình PuppeteerSharp;
  port-mode vẫn giữ làm opt-in/attach.
- Đã bỏ `CommandTimeout` khỏi option ở M1; M6 thêm lại đúng cách: một timer loop dùng chung
  (100 ms/tick) quét deadline, không cấp phát timer cho từng command.
- Kết quả typed trả `ReadOnlyMemory<byte>` (có cấp phát khi caller chọn lấy string); gate 0 B/op
  hiện áp dụng cho T0 (transport + dispatch), đúng như §2.
- M2 dùng `Target.setAutoAttach` + chờ `Target.attachedToTarget` cho `NewPageAsync` (giống
  Playwright) thay vì `Target.attachToTarget` thủ công: một đường duy nhất cho cả page tự tạo
  lẫn popup. `waitForDebuggerOnStart` vẫn bật nhưng router resume ngay: M5 không cần chèn gì vào
  page trước khi page chạy, vì toàn bộ JavaScript đi qua isolated world tạo lazy.
- M3 ban đầu **không chạy JavaScript nào trong page**: title/url lấy từ `Target.getTargetInfo` (1 round-trip); P6 đổi `TitleAsync` sang `document.title` trong isolated world để khớp Playwright và phản ánh thay đổi DOM tức thì, vẫn không bật `Runtime.enable`.
  content từ `DOM.getDocument` + `DOM.getOuterHTML` (2 round-trip).
- M3 dùng `Page.enable` (an toàn, không phải `Runtime.enable`/`Console.enable`) và
  `Page.setLifecycleEventsEnabled` để theo dõi load. `NetworkIdle` chưa làm vì cần đếm request.
- M4 **không cache root nodeId**: gọi `DOM.getDocument` lần nữa (việc mà đường pierce-shadow phải
  làm) sẽ làm node id cũ mất hiệu lực. Benchmark 200 click phát hiện lỗi "Could not find node with
  given id"; sửa bằng cách lấy root trước mỗi thao tác, tốn thêm 1 round-trip.
- M4 ban đầu chọn **pierce shadow là opt-in** (`Locator(sel, pierceShadowDom: true)`), vì đường thường
  để Chrome tự parse selector nhanh hơn snapshot. P1 đổi hành vi theo Patchright: locator thường vẫn
  dùng `DOM.querySelectorAll` làm fast path, nhưng khi trả 0 kết quả sẽ tự fallback sang snapshot
  `pierce:true` (throttle 200 ms) để chạm closed shadow root mà không inject script. Cờ
  `pierceShadowDom: true` vẫn giữ cho caller muốn ép đường snapshot ngay từ đầu.
- CSS subset hỗ trợ: type/#id/.class/[attr] với mọi operator, combinator ` ` `>` `+` `~`, list `,`,
  pseudo `:first-child`, `:last-child`, `:only-child`, `:nth-child`, `:nth-last-child`,
  `:nth-of-type`, `:first-of-type`, `:last-of-type`, `:not()`, `:is()`, `:root`, `:empty`,
  `:checked`, `:disabled`, `:enabled`. Pseudo-class phụ thuộc trạng thái live (`:hover`, `:focus`,
  `:visited`) và pseudo-element bị từ chối tường minh thay vì trả sai.
- M5 dùng `Runtime.evaluate` với `contextId` của isolated world, **không** `Runtime.enable`.
  `Page.frameNavigated` của main frame invalidate context; lỗi context cũ được retry một lần.
  Tên world là 32 ký tự hex ngẫu nhiên, không chứa "zerocdp"/"playwright"/"cdp".
- M5 thêm `CdpConnectionOptions.CommandObserver` để test ghi lại **toàn bộ** lệnh gửi đi và
  chứng minh trace không có `Runtime.enable`, `Console.enable`, `Debugger.enable`,
  `Target.setDiscoverTargets` hay `Page.addScriptToEvaluateOnNewDocument`.
- M5 `StealthProfile` đổi thành `Raw`/`Minimal`/`Default`. `Default` bỏ luôn `--disable-gpu`
  (dấu hiệu headless), nên khác `Minimal` ở đúng điểm có ý nghĩa.
- M6 phát hiện `FrameDispatcher` cũ dùng `Dictionary` không khoá từ nhiều thread (reader, caller,
  timer). Đã bọc lock và hoàn tất continuation **ngoài** lock; thêm test 200 command đồng thời.
- M6 `NavigationPolicy` chỉ chặn URL khởi đầu; redirect phía server cần `Fetch` interception (v2).
- Test sannysoft là opt-in (`ZEROCDP_E2E_NETWORK=1`), không phải merge gate; test local kiểm tra
  `navigator.webdriver == false` dưới profile `Default` và không có marker lạ trong main world.
- P0 chọn timeout mặc định **30 s**, poll **50 ms**; locator có thể override bằng
  `page.Locator(sel, timeout: ...)` và `page.DefaultTimeout`. Hết hạn ném `CdpTimeoutException`
  thay vì `ElementNotFoundException` tức thời như M4.
- Actionability P0 = attached + visible + enabled (+ editable cho `Fill`/`Type`). Chưa làm
  hit-target/occlusion và kiểm tra box ổn định hai frame; đây là giới hạn được ghi rõ trong README.
- Dialog mặc định tự dismiss khi **không có** subscriber. Có subscriber thì dialog giữ nguyên
  cho đến khi gọi `AcceptDialogAsync`/`DismissDialogAsync`; `DialogOpening` chạy trên reader thread.
- Iframe same-process dùng `Page.createIsolatedWorld(frameId)` + `Runtime.evaluate(returnByValue:false)`
  rồi `DOM.requestNode`, nên không cần `Runtime.enable`. Test thực nghiệm xác nhận
  `DOM.getContentQuads` trả **toạ độ tuyệt đối của main frame** cho iframe same-process lồng
  nhau; vì vậy không cộng offset thủ công. P2 đã pierce closed shadow root trong frame và đưa
  semantic `GetBy*` vào `FrameLocator`, dùng snapshot root tại `contentDocument` của frame owner
  và dừng traversal ở document lồng nhau để không rò sang frame khác.
- P4 OOPIF: browser-level `Target.setAutoAttach` **không** thấy iframe target; phải gửi
  `Target.setAutoAttach` trên session của page sở hữu iframe. Mỗi iframe OOPIF lồng nhau cần
  auto-attach riêng trên session cha. Chromium báo `targetId == frameId` cho iframe target, nên
  mapping frame → session có ngay khi attach; `Page.getFrameTree` xác nhận lại. Node id của child
  session được mã hoá `(contextId << 40) | realNodeId` để các thao tác DOM/JS sau đó route đúng
  session. Toạ độ `DOM.getContentQuads` của child là local; transform affine lấy từ content box
  của iframe owner trong session cha, rồi compose đệ quy cho OOPIF lồng nhau. Click dùng session
  cha với toạ độ main viewport; `BoundingBoxAsync` cũng trả toạ độ main viewport.
- P4 Playwright surface: thêm options/enums additive (`PageGotoOptions`, `LocatorClickOptions`,
  `WaitUntilState`, `LoadState`, `WaitForSelectorState`, `ScreenshotType`, `AriaRole`,
  `RoleOptions`, `LocatorWaitForOptions`, ...), `GetByRole(AriaRole)`, `WaitForURLAsync` extension,
  `InnerHTMLAsync`, screenshot/fill/click options, `SetDefaultTimeout(double)` theo ms. API native
  cũ giữ nguyên; không có reflection/serializer mới.
- P4 sample: `samples/ZeroCdp.Sample.RealWorld` tự mở HTTP server random port, launch browser profile
  riêng, chạy flow search → add to cart → checkout → receipt và ghi screenshot; chạy song song với
  test suite mà không tranh port/profile.
- Screenshot full-page lấy `cssContentSize` từ `Page.getLayoutMetrics`, giới hạn 16.384 px mỗi
  chiều và 64 triệu pixel; ảnh decode base64 trực tiếp từ frame UTF-8, không qua serializer.
- Protocol P0 bổ sung `Page.captureScreenshot`, `getLayoutMetrics`, `handleJavaScriptDialog`,
  `DOM.requestNode`, `resolveNode`, `DOM.focus`, `Input.insertText`, key modifiers/code và
  `Runtime.callFunctionOn` theo `objectId`. Toàn bộ vẫn là struct viết tay, không reflection.
- Sau P0: **123 test pass**, dispatch vẫn **0 B/op**; sample AOT chạy thêm fill/select/screenshot/
  dialog tự dismiss.
- P1 thêm **browser context** thật: `Target.createBrowserContext` + `disposeBrowserContext`, cookie
  qua `Storage.*` với `browserContextId`, page thuộc context và tự đóng khi context dispose. Context
  options (viewport, UA, locale, timezone, color scheme, geolocation, JS disable, CSP bypass, extra
  headers, proxy) được áp dụng trước navigation đầu tiên.
- P1 network: route dùng `Fetch.enable` **chỉ khi có ít nhất một route**; unmatched request hoặc
  handler return mà không act thì auto `continueRequest`; handler throw thì `failRequest` để page
  không bị treo. Observation/waiters dùng `Network.enable` theo yêu cầu. So khớp URL bằng glob
  managed, route đăng ký sau thắng. `Network.getResponseBody` trả `NetworkBody` (bytes + text).
- P1 navigation: `ReloadAsync`, `GoBackAsync`/`GoForwardAsync` qua `Page.getNavigationHistory` +
  `navigateToHistoryEntry`, `WaitForUrlAsync`, `WaitForLoadStateAsync`, `SetContentAsync`,
  `SetViewportSizeAsync`, `EmulateColorSchemeAsync`, `PdfAsync`, `BringToFrontAsync`,
  `SetBypassCspAsync`. History wait dùng commit signal, kể cả same-document navigation.
- P1 locator: `First`/`Last`/`Nth`, `AllTextContentsAsync`, `GetAttributeAsync`, `InputValueAsync`,
  `InnerTextAsync`, `ClickOptions` (button/modifiers/click count/delay), `DblClickAsync`,
  `SetInputFilesAsync` (kể cả clear qua JS cho input ẩn), `ScreenshotBytesAsync`/`ScreenshotAsync`
  bằng clip `Page.captureScreenshot`. Thêm `BoundingBoxAsync`, `FocusAsync`, `IsEnabledAsync`,
  `IsEditableAsync`, `IsCheckedAsync`, `IsHiddenAsync`.
- P1 semantic locator: `GetByText`, `GetByRole`, `GetByLabel`, `GetByPlaceholder`, `GetByTestId`.
  Chúng dùng snapshot `pierce:true` + matcher managed, nên closed shadow root cũng khớp; role/name
  hỗ trợ native HTML role phổ biến và accessible-name gần đúng (aria-label/labelledby/label/alt/
  placeholder/title). Đây là approximation có chủ đích, không phải full ARIA engine.
- Patchright refinement: fallback closed shadow tự động sau fast path, giữ **document order** cho
  `First`/`Last`/`Nth`; `EvaluateAsync(..., isolatedContext: false)` là opt-in main-world, mặc định
  vẫn isolated. Không bật `Runtime.enable`/`Console.enable` trong bất kỳ đường nào.
- P1 permissions: `Browser.grantPermissions`/`resetPermissions` theo `browserContextId`; geolocation
  cần grant origin và đã có e2e dùng `navigator.geolocation.getCurrentPosition`.
- Sau P1: **147 test pass**, dispatch vẫn **0 B/op**; sample AOT được mở rộng với context, route
  fulfill, semantic locator, box/focus, locator indexing, upload, main-world opt-in và element screenshot.
- P2 popup: chỉ target `type=page` có `openerId` mới được đăng ký vào context của opener;
  `WaitForPopupAsync`/`RunAndWaitForPopupAsync`, `PopupOpening`, `PageOpened`/`PageClosed`. Vì
  context id mặc định không được Chrome gắn vào target, popup được resolve qua opener page.
- P2 download: `Page.setDownloadBehavior(allowAndName)` theo page, dùng chung một thư mục tạm cho
  mỗi `CdpBrowser`; `WaitForDownloadAsync`/`RunAndWaitForDownloadAsync`, completion/save/delete.
- P2 file chooser: `Page.setInterceptFileChooserDialog`; waiter nhận chooser thì tắt interception,
  chọn file qua `DOM.setFileInputFiles` bằng `backendNodeId`, hỗ trợ nhiều file và cancel.
- P2 storage state: cookie + localStorage cho HTTP(S), JSON tương thích Playwright
  (`cookies`, `origins[].localStorage`) viết/đọc thủ công bằng `StringBuilder`/`JsonCursor`,
  `SaveAsync`/`LoadAsync` không dùng serializer. Import localStorage tạo target tạm và fulfill
  navigation tại từng origin để Chromium có frame đã commit trước khi ghi storage.
- P2 IndexedDB (`StorageStateOptions.IndexedDb`): snapshot mở từng database qua page JS, giữ
  `name/version/stores/indexes/records`; value/key JSON-compatible dùng `value`/`key`, còn Date,
  typed array, Map, Set, cycle dùng `valueEncoded`/`keyEncoded` theo wire format Playwright. Import
  cũng qua target tạm, xóa database cũ rồi tạo store/index và ghi record; không bật Runtime domain.
- P2 HAR replay: `RouteFromHARAsync` ở context/page, match method + URL exact/glob, phục hồi status,
  headers và body; `HarNotFoundAction.Abort` hoặc `Fallback`; strip các transfer header không an toàn
  (`content-length`, `content-encoding`, `transfer-encoding`, `connection`, `keep-alive`).
- P2 accessibility/ARIA: snapshot dùng computed AX tree của Chromium (`Accessibility.getFullAXTree`)
  thay vì tự suy đoán DOM; YAML lọc property nhiễu và text con trùng; locator snapshot chọn theo
  `backendDOMNodeId`.
- P2 Clock: fake timer chạy trong main world, được cài bằng document transformer (không dùng CDP
  virtual time hay `Page.addScriptToEvaluateOnNewDocument`), tự cài lại sau navigation; Clock API
  điều khiển qua `Runtime.evaluate` với `isolatedContext: false`.
- P2 video: `RecordVideoOptions { Dir, Size, FfmpegPath }`; `ScreencastHub` sở hữu một
  `Page.startScreencast` cho mỗi page và ack mỗi frame đúng một lần, nhiều consumer (video + trace)
  dùng chung. Frame JPEG được đẩy qua channel sang ffmpeg (libvpx-vp9) trên background,
  `Page.Video.PathAsync()` chờ file sau khi page đóng. Không dùng screenshot polling và không thêm
  lệnh stealth bị cấm.
- P2 tracing: `BrowserContext.Tracing.StartAsync/StopAsync` ghi zip gồm `trace.trace` (JSONL
  context-options, page/navigation/snapshot/source event, screencast-frame liên tục),
  `trace.network` (resource-snapshot dạng HAR), `screencast/*.jpeg`, `snapshots/*.mhtml` và
  `sources/*`. Frame trace được sample ~5 fps, ghi file tạm rồi stream vào zip để giới hạn RAM.
  `StopChunkAsync` đóng chunk hiện tại và tự mở chunk mới; `trace.network` tích lũy theo session.
  Snapshot dùng `Page.captureSnapshot("mhtml")`, source lấy qua `Network.getResponseBody`, theo
  `sourceMappingURL` và giới hạn 4 MiB/resource. Đây là format thực dụng, không tương thích đầy đủ
  với Playwright trace viewer/snapshot schema.
- P2 routing context: `RouteRegistry` dùng chung; page route ưu tiên hơn context route, route mới
  nhất thắng trong từng scope. `Fetch.enable` chỉ bật khi có route; page mới có context route được
  defer navigation để request đầu tiên bị intercept. Context `Request`/`Response`/`RequestFailed`
  được forward từ mọi page, kèm `WaitForRequestAsync`/`WaitForResponseAsync` và `EnableNetworkAsync`.
- P2 frame: `FindFirstAsync`/`CountAsync` dùng chung `ResolveAsync`, nên semantic và fallback
  closed-shadow hoạt động cả trong `FrameLocator`; `WithIndex` giữ frame scope cho locator semantic.
- P2 locator: `ClearAsync`, `PressSequentiallyAsync`, `SelectTextAsync`, `BlurAsync`,
  `DispatchEventAsync`, `EvaluateAsync`, `DragToAsync`, `SetCheckedAsync`,
  `ScrollIntoViewIfNeededAsync`, `AllInnerTextsAsync`, `TextContentAsync`; page
  `WaitForFunctionAsync`/`WaitForSelectorAsync`/`WaitForTimeoutAsync`; semantic `GetByAltText`/
  `GetByTitle` ở page và frame.
- Sau P2: **156 test pass**, dispatch vẫn **0 B/op**; AOT sample chạy thêm frame/popup/file chooser/
  download/context route/storage state/wait helper; publish **6.7 MiB**, package **192 KB**.
- P3 hoàn tất selector/Patchright parity và nâng lên **179 test**. P4 thêm OOPIF trực tiếp và lồng
  nhau, Playwright options/enums, sample thực tế; tổng **181 test**, dispatch vẫn **0 B/op**.
  Test OOPIF dùng hai `LocalHttpServer` khác site (`127.0.0.1` và `[::1]`) để Chromium thực sự tách
  process; test kiểm tra locator, fill, click, `BoundingBoxAsync`, evaluate và transform lồng nhau.
- Rà soát parity Playwright lần cuối: bổ sung handle action/state (`TapAsync`, `SelectTextAsync`,
  `DispatchEventAsync`, `InnerHTMLAsync`, `CheckAsync`/`UncheckAsync`/`SetCheckedAsync` với options,
  `IsEnabledAsync`/`IsDisabledAsync`/`IsEditableAsync`), `Worker.EvaluateHandleAsync`, waiter
  `WaitForRequestFinishedAsync`/`WaitForRequestFailedAsync` cùng event `RequestFinished`,
  `WaitForNavigationAsync`/`RunAndWaitForNavigationAsync`, `ViewportSize`, `UnrouteAllAsync`,
  `Download.CancelAsync`/`UrlAsync`/`SuggestedFilenameAsync`, cookie context có filter
  (`CookiesAsync`, `AddCookiesAsync`, `ClearCookiesAsync(options)`, `ClearGeolocationAsync`),
  `APIResponse.HeadersAsync`/`HeadersArrayAsync`, `APIRequestContext.StorageStateAsync` và
  `DragAndDropAsync`. Tổng **228 test**, dispatch vẫn **0 B/op**.
- Quản lý tab kiểu Playwright: `PageOpened` cùng alias `Page` nổ cho cả `NewPageAsync` lẫn popup
  (tab có mặt trong `Pages` trước khi handler chạy), `WaitForPageAsync`/`RunAndWaitForPageAsync`
  trên context, `CloseAsync(PageCloseOptions { RunBeforeUnload })` đi qua `Page.close` nên tab có
  thể vẫn mở khi hook/dialog chặn, `Browser.CloseAsync` và `LaunchedBrowser.CloseAsync`.
  Tổng **237 test**, gồm test live `https://example.com` với Chromium thật (tự bỏ qua khi offline).
- Sửa race double-complete trong `PendingCommand`: callback huỷ từ `CancellationToken` có thể chạy
  sau khi response đã hoàn tất command và làm `SetException` ném trên thread timer, giết process.
  Mọi đường hoàn tất giờ đi qua `Interlocked` một lần duy nhất; khi huỷ thắng, entry được `Abandon`
  trước khi slot về pool nên response đến muộn không thể ghi đè command tái sử dụng. Có 2 test
  regression trong `FrameDispatcherTests`. Suite chạy 3 lần liên tiếp 237/237; AOT sample publish +
  chạy thật exit 0.

**Việc tối ưu còn lại đã biết**: scan JSON hiện là scalar (~1.5 ns/byte). Với frame lớn (>8 KB)
viết lại bằng `Vector256` sẽ có lợi; với frame CDP thường gặp thì lợi ích nhỏ vì Chrome là nút cổ chai.
`FrameDispatcher` giờ có lock cho pending table; đó là chi phí trên response path, không phải event
dispatch path, và đã được kiểm chứng bằng test 200 command đồng thời. Nếu sau này cần tối ưu thêm,
có thể thay bằng open-addressing table đúng như §3.2.


## 1. Mục tiêu & phạm vi

| Hạng mục | Quyết định |
|---|---|
| Runtime | `net10.0` only (không multi-target) |
| Browser | Chromium-family (Chrome, Chromium, Edge) — **chỉ CDP** |
| Deploy | `PublishAot=true`, `IsAotCompatible`, `EnableTrimAnalyzer`, single-file |
| Dependencies | **Test project:** 0 package runtime ngoài BCL. Không `Newtonsoft`, không `System.Text.Json` serializer, không `Playwright`, không driver Node |
| Transport | CDP trên `--remote-debugging-pipe` (mặc định) + WebSocket (fallback / attach) |
| Stealth | Tương đương Patchright, tự viết lại (Apache-2.0 của họ chỉ để tham chiếu hành vi) |
| License dự kiến | MIT (core) — không copy code Patchright/Playwright |

### Không nằm trong phạm vi (v1)
- Firefox/WebKit (cần WebDriver BiDi, khác hoàn toàn).
- Browser tự tải về. v1 chỉ *phát hiện* browser có sẵn (`CHROME_PATH` + scan path chuẩn).
- Playwright React trace viewer UI, codegen và Playwright Test runner vẫn ngoài phạm vi. `TraceArchive` cung cấp reader/schema/viewer self-contained; snapshot hiện là MHTML thực dụng thay vì schema layout/ARIA của Playwright.

---

## 2. Sự thật kỹ thuật phải chấp nhận (để không hứa sai)

"Zero alloc" toàn bộ API là **bất khả thi**: CDP trả URL/title/text dưới dạng JSON, và API kiểu
Playwright trả `string`/object cho người dùng. Vì vậy định nghĩa hợp đồng theo tầng:

| Tầng | Nội dung | Ngân sách alloc | Cách đo |
|---|---|---|---|
| **T0 — Transport + dispatch** | đọc pipe, tách frame `\0`, parse `id/method/sessionId`, đẩy vào bảng route, ghi command ra buffer | **0 B/op** (hard assert) | `GC.GetAllocatedBytesForCurrentThread()` trong test |
| **T1 — Typed CDP** | struct kết quả, `ValueTask<T>`, `IValueTaskSource`, pooled builder | **0 B/op** nếu consumer đọc bằng `Span`/struct | test + BenchmarkDotNet `MemoryDiagnoser` |
| **T2 — Page/Locator ergonomics** | `IPage`, `ILocator`, `string` trả về | alloc bình thường, có ghi chú | benchmark tham chiếu |

Quy tắc thiết kế để đạt T0/T1:
- **Không bao giờ** materialize `string` trên đường đi nội bộ — mọi thứ là UTF-8 `ReadOnlySpan<byte>`.
- Kết quả command chỉ hợp lệ **trong lúc callback chạy** (`ref struct CdpResult`). Muốn giữ lại phải
  copy tường minh vào struct/target do caller cấp. Đây là điều kiện bắt buộc để có 0 alloc.
- Không `JsonDocument`, không `JsonElement`, không `dynamic`, không `Activator`, không reflection.

---

## 3. Kiến trúc

```
ZeroCdp (core)
├─ Transport/      StreamTransport (pipe fd 3/4 / Windows handles), WebSocketTransport (loopback/attach)
├─ Json/           JsonCursor (UTF-8 tokenizer), JsonUtf8 (writer/escape)
├─ Runtime/        CdpConnection, FrameDispatcher, SessionRouter, SessionTable, PendingCommand
├─ Protocol/       viết tay: Page/Runtime/DOM/Target/Cookie params + result readers
├─ Browser/        BrowserLauncher, CdpBrowser, BrowserContext, BrowserContextOptions,
│                  CdpPage, Locator, FrameLocator, NetworkState/Route, PageState,
│                  ScreenshotOptions, DialogInfo, InputDispatcher, Keyboard,
│                  LaunchOptions/LaunchArguments, ProfileDirectory
├─ Dom/            DomNode, DomTreeParser (snapshot pierce shadow)
├─ Selectors/      CssSelector parser, SelectorMatcher
├─ Stealth/        IsolatedWorldRuntime, WorldNameFactory
└─ Security/       NavigationPolicy, LogRedactor
```

Không có project `ZeroCdp.Generators`: protocol layer được viết tay (xem §3.3).

### 3.1 Transport — `--remote-debugging-pipe`

Lý do chọn pipe làm mặc định:
- **Không mở TCP port nào** → không có bề mặt tấn công localhost (malware/process khác không
  `connect` được vào debug endpoint), và Chrome 136+ đã siết `--remote-debugging-port` với
  profile mặc định (buộc phải có `--user-data-dir` riêng — ta luôn dùng thư mục riêng, mode 0700).
- Không cần `--remote-allow-origins`, không phụ thuộc Host-header check của Chrome.
- Framing đơn giản: `UTF8 JSON` + `0x00` → tách frame bằng `SearchValues<byte>.IndexOf` (SIMD,
  0 alloc), feed thẳng vào `PipeReader`/`SequenceReader<byte>`.

Spawn tiến trình (zero-alloc, không qua `System.Diagnostics.Process`):
- Linux/macOS: `[LibraryImport]` → `posix_spawnp` + `posix_spawn_file_actions_adddup2(fa, readFd, 3)`,
  `adddup2(writeFd, 4)`, `posix_spawnattr` để reset signal mask. `dup2` tự clear `FD_CLOEXEC`.
- Windows: `AnonymousPipeServerStream` với `HandleInheritability.Inheritable`, lấy handle client
  dạng thập phân và truyền `--remote-debugging-io-pipes={readHandle},{writeHandle}`. Chromium
  `AdoptPipes` kiểm tra `FILE_TYPE_PIPE` rồi `_open_osfhandle`; handle giữ nguyên giá trị vì được
  kế thừa. Ngay sau `Process.Start`, gọi `DisposeLocalCopyOfClientHandle()` để đóng bản sao phía
  parent và cho child thấy EOF đúng lúc. Đây là mô hình PuppeteerSharp đang dùng.
- Port mode vẫn là opt-in (`CdpTransportKind.Port`) và là đường attach/loopback WebSocket, dùng
  `--remote-debugging-port=0` + `DevToolsActivePort`, bind 127.0.0.1.

`SafeHandle` cho pipe + process → không rò fd khi dispose/finalize.

### 3.2 Runtime — dispatch 0 alloc

- **1 reader loop** / connection (không thread-pool per message), đọc vào `ArrayPool` buffer.
- Parse top-level **không** dựng cây: tokenizer UTF-8 chỉ lấy `id` (ulong), `sessionId` (span),
  `method`/`event` (span), rồi **bàn giao nguyên slice `params`/`result`** cho handler.
- **Route table**: key = hash 64-bit của `(domain, method)` → static function pointer
  (`delegate*<ref CdpEventReader, void>`). Không `Dictionary<string, Delegate>`, không string
  comparison, không boxing.
- **CommandTable**: open-addressing array, key `ulong id` (số nguyên, không string như Playwright),
  value = `ICommandContinuation` ở dạng `struct` + `PoolingAsyncValueTaskMethodBuilder<T>` /
  `ManualResetValueTaskSourceCore<T>`. → `await` trên hot path không cấp phát state machine.
- **Outbound**: command ghi trực tiếp vào pooled `byte[]`; string escape bằng writer SIMD tự viết
  (`JsonWrite.Utf8String(ReadOnlySpan<char>)` + fast path khi toàn ASCII), không `Utf8JsonWriter`.
- **Session id** interns vào pool cố định; demux theo `sessionId` không cấp phát.

### 3.3 Protocol layer — viết tay

Thực tế M1–M7 dùng protocol layer viết tay thay vì Roslyn generator + snapshot
`browser_protocol.json`:
- `readonly struct` params/result cho các domain đang dùng (Page, Runtime, DOM, Target, Cookie,
  Storage, Fetch, Network, Emulation, Browser).
- `ICdpCommandParams.Write(ref CommandWriter)` và `ICdpResult.TryRead(...)`.
- Method name là hằng UTF-8 trong từng struct; không reflection, không serializer, AOT-safe tuyệt đối.
- Field lạ trong response bị bỏ qua, nên Chrome thêm field không làm vỡ parser.

Lý do: surface v1 nhỏ, chi phí duy trì generator + snapshot lớn hơn lợi ích. Khi mở rộng domain ở v2,
có thể thêm generator sinh đúng các struct này mà không đổi tầng Runtime. R3 được giảm thiểu bằng
cách pin Chrome version trong test và parser chấp nhận field lạ.

---

## 4. Stealth layer — map patch Patchright → cách làm của ZeroCDP

Đây là lý do tồn tại của thư viện: Playwright bị phát hiện chủ yếu qua **hành vi CDP**, không phải
qua JS injection. Bảng dưới là thiết kế tương ứng từng patch.

| Patch của Patchright | Cơ chế | ZeroCDP |
|---|---|---|
| **`Runtime.enable` leak** (lớn nhất) | Playwright gọi `Runtime.enable` → page thấy `Runtime.consoleAPICalled`/stack-trace artifact | **Không bao giờ** gọi `Runtime.enable`. Mọi evaluate mặc định dùng `contextId` của **isolated world** (`Page.createIsolatedWorld`). Track frame lifecycle bằng `Page.frameNavigated`/`frameAttached` thay vì `executionContextCreated`. `EvaluateAsync(..., isolatedContext:false)` là opt-in main-world và vẫn không bật domain |
| **`Console.enable` leak** | patchright tắt hẳn Console domain | Không gọi `Console.enable`. Capture console qua `Log.enable` (off mặc định, opt-in) hoặc in-page hook (document là detectable) |
| **Network domain noise** | Playwright bật Network cho mọi page | Chỉ bật `Network.enable` khi caller observe/wait response. `Fetch.enable` chỉ tồn tại khi có route và tự disable khi route cuối bị gỡ |
| **Command flag leaks** | thêm `--disable-blink-features=AutomationControlled`; bỏ `--enable-automation`, `--disable-component-update`, `--disable-default-apps`, `--disable-extensions`, `--disable-popup-blocking` | `LaunchArguments`: preset `Default` (đúng danh sách trên + bỏ `--disable-gpu`), `Minimal`, `Raw`. Mỗi flag kèm doc về đánh đổi |
| **`Target.setDiscoverTargets` / auto-attach noise** | không bật discovery | Chỉ `Target.setAutoAttach {flatten:true, waitForDebuggerOnStart:true}` trên endpoint browser; tắt `Target.setDiscoverTargets` |
| **Injected script leak** (Playwright nhúng `utilityScript` vào **main world**) | patchright dùng isolated context | **Không inject gì vào main world.** Selector resolve bằng `DOM.querySelectorAll` + `Runtime.callFunctionOn` trên `objectId` trong isolated world. Đây là lợi thế lớn nhất và cũng là lý do thư viện nhanh hơn |
| **`sourceURL` / `//# sourceURL`** | patchright strip | Command writer không bao giờ thêm `sourceURL`; `evaluate` chỉ gửi `expression` |
| **Closed Shadow Roots** | patchright hỗ trợ locator trong closed shadow | Locator thường query trước; nếu 0 match thì fallback `DOM.getDocument(depth:-1, pierce:true)` + CSS matcher C#, throttle 200 ms — không cần JS và không có flag opt-in. `pierceShadowDom:true` vẫn ép snapshot ngay |
| **Không fingerprint injection** | khuyến nghị dùng Chrome thật + persistent profile | `Emulation.setTimezoneOverride/setLocaleOverride/setGeolocationOverride`; **mặc định không** override UA/WebGL/canvas. `no_viewport` + persistent context được hỗ trợ first-class |
| `navigator.webdriver` | nhờ flag `AutomationControlled` | **Không patch JS global** (tự nó là dấu hiệu detection). Chỉ dựa vào flag removal |

Nguyên tắc: *không thêm bất kỳ thứ gì vào page mà page có thể nhìn thấy*, trừ khi người dùng opt-in.

---

## 5. Security layer

Bảo mật theo nghĩa **của thư viện automation**, không phải né detection:

1. **Không debug port** (pipe mặc định). Nếu dùng port: force `127.0.0.1`, port ngẫu nhiên,
   không bao giờ `--remote-allow-origins=*`.
2. **`--user-data-dir` riêng**, tạo bằng `Directory.CreateDirectory` + `chmod 700`, xoá khi dispose
   (tắt được bằng option). Không đụng profile Chrome thật của người dùng.
3. **Sandbox bật mặc định.** `--no-sandbox` phải opt-in tường minh + log warning.
4. **Không auto-download browser** (giảm supply-chain). Locator chỉ đọc path có sẵn.
5. **`ResourceLimits`** chống DoS từ page độc hại: cap kích thước message CDP (mặc định 32 MB),
   cap số pending command, cap `DOM.getDocument` depth, timeout mặc định cho mọi command,
   `CancellationToken` xuyên suốt.
6. **`LogRedactor`**: quét JSON UTF-8, che giá trị của các key nhạy cảm (`password`, `cookie`,
   `setCookie`, `Authorization`, `token`, `Input.insertText.value`,
   `Page.handleJavaScriptDialog.promptText`, ...) ở mọi độ sâu; input
   malformed bị thay bằng placeholder, không bao giờ log raw. Không log raw frame ở level
   `Information`.
7. **`NavigationPolicy`**: hook trước khi navigate/request, cho phép allow-list origin — chặn
   exfiltration sang domain lạ khi chạy untrusted page.
8. **Certificate**: không bao giờ `Security.setIgnoreCertificateErrors` trừ khi opt-in; expose
   SPKI-pin hook cho `Fetch` interception (v2).
9. **AOT + không reflection** = bề mặt tấn công nhỏ hơn (không deserialize type tuỳ ý).
10. **`SafeHandle` + `IAsyncDisposable`** bắt buộc; không finalizer tự viết → không rò fd/process.
11. **Invariant globalization** (`InvariantGlobalization=true`) → giảm phụ thuộc ICU, kết quả
    deterministic cross-platform.

---

## 6. Cấu trúc repo

```
ZeroCDP/
├─ Directory.Build.props        (net10.0, nullable, AOT analyzers, warnaserror nội bộ)
├─ ZeroCDP.slnx
├─ src/
│  └─ ZeroCdp/                  (core; không có generator project)
├─ tests/
│  ├─ ZeroCdp.Tests/            (xunit — unit, alloc assertion, e2e chromium thật)
│  └─ ZeroCdp.Benchmarks/       (BenchmarkDotNet)
└─ samples/
   ├─ ZeroCdp.Sample.AotConsole/   (NativeAOT smoke)
   └─ ZeroCdp.Sample.RealWorld/    (storefront e2e, tự mở HTTP server)
```

E2e dùng chromium thật nằm chung `ZeroCdp.Tests` (tự return sớm nếu máy không có browser), không tách
project `ZeroCdp.Conformance`.

---

## 7. Milestones & gate

| # | Nội dung | Gate (phải chứng minh) |
|---|---|---|
| **M0** | Skeleton, props, CI, AOT smoke publish | `dotnet publish -r linux-x64` ra binary AOT, chạy in "hello" |
| **M1** | `NullDelimitedReader` + `CommandBuffer` + spawn pipe + `CommandTable` + reader loop | `Browser.getVersion` OK qua pipe; **0 B/op** cho 10k event dispatch (test assert) |
| **M2** | Session/target: `Target.createTarget`, `attachToTarget(flatten)`, demux sessionId | mở 2 page, nhận event đúng page, 0 alloc demux |
| **M3** | Page core: navigate, lifecycle, cookie, close/dispose | e2e `goto` + `Content` + `Title` trên chromium thật |
| **M4** | Selectors (CSS tự viết) + Locator + input dispatch + closed shadow root | click/type/scroll hoạt động trên DOM thật + shadow DOM test |
| **M5** | Stealth: isolated world, không `Runtime.enable`, arg profiles | verify bằng harness JS kiểm tra **không** có `Runtime.enable` trong trace CDP; isolated world name không lộ; sannysoft trong CI e2e (không gate merge) |
| **M6** | Security: redaction, limits, policy, sandbox check | test DoS (message khổng lồ → bị cắt an toàn), test redaction |
| **M7** | Benchmarks, package, docs, sample AOT | benchmark publish; `dotnet pack`; sample chạy AOT |
| **P0** | Playwright-style surface: screenshot, dialog, iframe same-process, auto-wait, fill/check/hover/select/press | e2e trên Chromium thật; screenshot PNG/JPEG/full-page, dialog accept/dismiss/auto, iframe lồng nhau, wait visible/hidden/enabled, 123 test pass; T0 vẫn 0 B/op |
| **P1** | Context, network, navigation/locator ergonomics, semantic locators, permissions/geolocation, upload, element screenshot, Patchright refinements | e2e route fulfill/continue/abort, response body, context isolation/cookies, history, closed-shadow auto fallback, main-world opt-in; **147 test pass**; T0 vẫn **0 B/op** |
| **P2** | Popup/download/file chooser, storage state, context route/event, frame closed-shadow + semantic, locator edit/eval/drag, wait helper | e2e popup/download/chooser, JSON storage round-trip, context route initial navigation + page precedence, context observation across pages, frame semantic/closed-shadow, drag; **156 test pass**; T0 vẫn **0 B/op** |
| **P3** | Selector engine parity + Patchright XPath, actionability hit-target/stable box, networkidle, page keyboard/mouse, route fallback/upgrade, context network helpers, ExposeFunction, AddInitScript không dùng `addScriptToEvaluateOnNewDocument`, connectOverCDP | XPath trong closed shadow + frame; che khuất bị từ chối rồi click lại sau khi gỡ overlay; box ổn định; networkidle chờ fetch trễ; route fallback; init script chạy trước script của page và trace không có lệnh inject; attach port-mode; **179 test pass**; T0 vẫn **0 B/op** |

Thứ tự cố ý: **pipe + zero-alloc trước** vì nó là rủi ro kỹ thuật cao nhất và là nền của mọi thứ.

---

## 8. API sơ bộ (T2)

```csharp
await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(new LaunchOptions
{
    Headless = true,
    Stealth  = StealthProfile.Default,     // preset Patchright
    NavigationPolicy = new NavigationPolicy("https://example.com"),
});
await using CdpPage page = await launched.Browser.NewPageAsync();
await page.GotoAsync("https://example.com");

var locator = page.Locator("button.submit");        // CSS parser C#, không inject script
await locator.ClickAsync();

string title = await page.TitleAsync();              // T2: string trả về, có alloc

// M5 — JavaScript trong isolated world, không Runtime.enable
EvaluationResult result = await page.EvaluateAsync("document.querySelectorAll('a').length");
if (result.Value.TryGetInt64(out long links))
{
    Console.WriteLine($"{links} links");
}

// P0 — actionability + trusted input, screenshot, dialog, iframe
await page.Locator("#name").FillAsync("Ada");          // poll visible/enabled/editable
await page.Locator("#terms").CheckAsync();
await page.Locator("#plan").SelectOptionAsync("pro");
await page.Locator("#name").PressAsync("Enter");
await page.ScreenshotAsync("page.png", new ScreenshotOptions { FullPage = true });

FrameLocator frame = page.FrameLocator("iframe#checkout");
await frame.Locator("button.pay").ClickAsync();

// P1 — context, network, locator ergonomics, Patchright closed-shadow fallback
await using BrowserContext context = await launched.Browser.NewContextAsync(new BrowserContextOptions
{
    Viewport = new ViewportSize(1280, 800),
    ColorScheme = PreferredColorScheme.Dark,
});
await context.GrantPermissionsAsync("geolocation", "https://example.com");
await using CdpPage isolated = await context.NewPageAsync("https://example.com");

await isolated.RouteAsync("**/api/**", route => route.FulfillAsync(200, "{}", "application/json"));
await isolated.Locator(".row").Nth(1).ClickAsync();
string[] rows = await isolated.Locator(".row").AllTextContentsAsync();
await isolated.Locator("#upload").SetInputFilesAsync("/tmp/file.pdf");
byte[] card = await isolated.Locator(".card").ScreenshotBytesAsync();

// Normal locator reaches closed shadow roots; main-world evaluation is explicit.
await isolated.Locator("#inside-closed-shadow").ClickAsync();
await isolated.EvaluateAsync("window.appState", isolatedContext: false);

// P2 — popup, download, file chooser, storage state, context route/event, frame locator
CdpPage popup = await isolated.RunAndWaitForPopupAsync(
    () => isolated.Locator("#open-popup").ClickAsync());
Download download = await isolated.RunAndWaitForDownloadAsync(
    () => isolated.Locator("#export").ClickAsync());
FileChooser chooser = await isolated.RunAndWaitForFileChooserAsync(
    () => isolated.Locator("#pick-file").ClickAsync());
await chooser.AcceptAsync("/tmp/file.pdf");

StorageState state = await context.StorageStateAsync();
await state.SaveAsync("/tmp/state.json");
await context.SetStorageStateAsync(await StorageState.LoadAsync("/tmp/state.json"));

// Opt-in IndexedDB, ARIA snapshot, Clock and HAR replay.
StorageState fullState = await context.StorageStateAsync(
    new StorageStateOptions { IndexedDb = true });
string aria = await isolated.Locator("main").AriaSnapshotAsync();
await isolated.Clock.InstallAsync();
await context.RouteFromHARAsync("/tmp/session.har");

await context.RouteAsync("**/api/**", route => route.FulfillAsync(200, "{}", "application/json"));
Task<PageResponse> contextResponse = context.WaitForResponseAsync("**/api/**");
FrameLocator frame2 = isolated.FrameLocator("iframe#checkout");
await frame2.GetByRole("button", "Pay").ClickAsync();
await isolated.Locator("#drag").DragToAsync(isolated.Locator("#drop"));
await isolated.WaitForFunctionAsync("document.body.dataset.ready === 'yes'");
```

---

## 9. Rủi ro & giảm thiểu

| # | Rủi ro | Mức | Giảm thiểu |
|---|---|---|---|
| R1 | Windows: map pipe handles sang process con | Cao | **Đã triển khai** bằng inherited `AnonymousPipeServerStream` + `--remote-debugging-io-pipes`; chưa có máy Windows để verify runtime, port mode vẫn làm fallback |
| R2 | `posix_spawn` file-actions struct layout khác nhau giữa glibc/musl/macOS | Trung bình | Đã verify trên glibc (512-byte aligned blob). **Chưa test musl/macOS** |
| R3 | Chrome đổi CDP schema giữa các version | Trung bình | Protocol viết tay bỏ qua field lạ; pin Chrome version trong test; thêm generator khi surface mở rộng |
| R4 | "Undetectable" là cuộc đua vũ trang, không phải trạng thái | Cao (về kỳ vọng) | Document rõ: đây là *hành vi giống người dùng thật*, không phải cam kết vượt mọi detector |
| R5 | 0 alloc dễ vỡ khi thêm feature | Trung bình | `AllocAssert` trong CI + BenchmarkDotNet baseline chặn regression |
| R6 | Không có máy Windows/macOS để verify | Trung bình | CI matrix GitHub Actions sau khi user chấp nhận; local chỉ verify Linux |

---

## 10. Quyết định đã chốt

1. **API surface**: **một package `ZeroCdp`** — core CDP và Playwright-style wrapper nằm chung assembly
   để tránh version skew; T0/T1 vẫn là struct/span API, T2 là `CdpPage`/`Locator`.
2. **Windows**: pipe là mặc định giống POSIX, dùng inherited anonymous-pipe handles; port mode là opt-in/fallback.
3. **Zero alloc**: **gate cứng T0 + T1** (`0 B/op` assert trong CI). T2 alloc bình thường.
4. **License**: **MIT**.

---

## 11. P3 — phần Playwright còn thiếu (kế hoạch)

Trạng thái xuất phát của kế hoạch này: **158 test pass** trên chromium thật (linux-x64, Release),
T0 vẫn **0 B/op**. Mục tiêu P3 là đóng ba khoảng trống lớn nhất so với Playwright **mà vẫn giữ
nguyên mô hình stealth của Patchright** (không `Runtime.enable`, không `Console.enable`, không
`Page.addScriptToEvaluateOnNewDocument`, không inject vào main world).

### 11.1 Khoảng trống đối chiếu Playwright

| Nhóm | Playwright có, ZeroCDP chưa có | Xử lý |
|---|---|---|
| Selector engine | prefix `css=`, `xpath=`, `text=`, `id=`, `data-testid=`, `role=`; **XPath trong closed shadow root** (Patchright mới thêm) | **P3a** |
| CSS mở rộng | `:has-text()`, `:visible`, `:nth-match()` | P3a (phần rẻ: `:has-text()`, `:visible`) |
| Locator lọc | `filter({ hasText, has, hasNotText, visible })`, `and()`, `all()`; `getByRole` với `level/checked/disabled/expanded/pressed/selected/includeHidden` | **P3a** |
| Actionability | hit-target/occlusion và box ổn định 2 frame | **P3b** |
| Chờ | `waitUntil: 'networkidle'`, `waitForLoadState('networkidle')` | **P3b** |
| Input surface | `page.keyboard.*`, `page.mouse.*` (Move/Down/Up/Wheel) công khai | P3b |
| Route nâng cao | `fallback()`, `continue()` override header/method/postData, `fulfill()` nhận `byte[]`/path | P3b |
| Context | `setExtraHTTPHeaders`, `setOffline`, `setGeolocation`, `emulateMedia` ở mức context | P3b |
| Script/hàm trong page | `exposeFunction`, `exposeBinding`, `addInitScript` | **P3c** |
| Attach | `connectOverCDP(wsEndpoint)` để lái Chrome thật / persistent profile | **P3c** |
| OOPIF | iframe cross-site và lồng nhau, child target session, synthetic node id, affine transform | ✅ P4 |
| Console/pageerror | `page.on('console')`, `page.on('pageerror')` | **Không làm** — cần `Runtime.enable`/`Console.enable`, đúng như Patchright; giữ `Log.enable` opt-in hiện có |
| Tracing/video/HAR/codegen | — | HAR, tracing nền tảng (frame/snapshot/source/chunk), video và trace reader/viewer self-contained đã có; React trace viewer của Playwright, codegen và test runner ngoài phạm vi v1 |

### 11.2 P3a — selector parity + XPath closed shadow (Patchright)

Đây là mục "kết hợp Patchright" rõ nhất: Patchright hỗ trợ **XPath xuyên closed shadow root**, thứ
mà approach snapshot hiện tại chưa có.

1. **Bộ định tuyến selector** (`Selectors/SelectorEngine.cs`, mới):
   - Nhận diện prefix `css=`, `xpath=`, `text=`, `id=`, `data-testid=`, `role=`.
   - Không có prefix: `//…` hoặc `..` → XPath; còn lại → CSS (giống Playwright).
   - `Locator(string)` parse eager như hiện nay, nên selector sai vẫn ném ngay tại call site.
   - API công khai không đổi: `page.Locator("//button[@type='submit']")` chạy được.
2. **Fast path XPath**: `DOM.performSearch` + `DOM.getSearchResults` + `DOM.discardSearchResults`
   (struct protocol mới trong `Protocol/DomParams.cs`). `searchId` luôn được discard trong `finally`
   để không rò search toàn cục; hai locator XPath song song phải độc lập (test).
3. **Fallback closed shadow**: interpreter XPath chạy trên snapshot `DomNode` (`DomTreeParser` đã
   dựng sẵn `Children`/`ShadowRoots`/`Parent`/`Attributes`/`NodeValue`). Subset hỗ trợ:
   trục `child`/`descendant`/`descendant-or-self`/`self`/`parent`, node test `*`, tên, `text()`,
   `node()`, predicate vị trí `[n]`/`[last()]`, `[@a]`, `[@a='v']`, `contains()`, `starts-with()`,
   `normalize-space()`, `text()='v'`, `not()`, `and`/`or`. Trục khác (`following-sibling`, …) bị
   từ chối tường minh bằng `CdpException` thay vì trả kết quả sai.
   Kết quả giữ **document order** như đường CSS, để `First`/`Last`/`Nth` nhất quán.
   Đây là bản C# thuần, không inject JS — lợi thế stealth giữ nguyên.
4. **`text=` engine**: exact (`text="…"`) và substring (chuẩn hoá whitespace), dùng lại
   `TextNormalizer`/matcher của semantic locator; hỗ trợ cả trong frame.
5. **CSS mở rộng**: `:has-text("…")`, `:visible` (kiểm tra qua snapshot: display/visibility/size),
   `:not()`/`:is()` đã có. `:nth-match()` để lại P4.
6. **Locator API**:
   - `Filter(LocatorFilterOptions { HasText, HasNotText, Has, Visible })` — lọc trên tập match hiện tại.
   - `And(Locator)` — giao hai tập, giữ document order.
   - `AllAsync()` → `IAsyncEnumerable<Locator>` (`CountAsync` + `Nth`, không snapshot lại).
   - `GetByRole` overload mới `RoleOptions { Name, Exact, Level, Checked, Disabled, Expanded,
     Pressed, Selected, IncludeHidden }`; overload cũ giữ nguyên để không phá API.
   - `LocatorSpec` thêm trường lọc cho role; accessible-name vẫn là approximation có chủ đích.

**Gate P3a**: e2e XPath tuyệt đối/tương đối, XPath có predicate thuộc tính và `text()`, XPath trong
closed shadow (host tạo closed root bằng JS trong test), `text=`, `filter`/`and`/`all`, `getByRole`
với `level` và `checked`, XPath trong `FrameLocator`. Dự kiến **158 → ~174 test**, T0 vẫn 0 B/op.

### 11.3 P3b — actionability, networkidle, input & route nâng cao

1. **Hit-target**: trước khi dispatch, lấy điểm click từ `DOM.getContentQuads` (đã có), rồi
   `DOM.getNodeForLocation` (struct mới) → `backendNodeId`; chấp nhận khi hit node **là** target
   hoặc là **tổ tiên** của target. Bị che → poll tiếp tới timeout rồi ném `CdpTimeoutException`
   (đúng hành vi Playwright: retry, không fail ngay).
   - Ngoại lệ có chủ đích: target nằm trong closed shadow root (hit trả về host) → bỏ qua check và
     ghi chú trong README, vì không có cách xác định occlusion bên trong root mà không inject JS.
   - Option `Actionability { HitTarget = true, StableBox = true }` trên page để tắt khi cần tốc độ.
2. **Stable box**: so `getContentQuads` hai lần cách một animation frame (≈16 ms) trước khi click;
   element đang animate sẽ được chờ thay vì click vào toạ độ cũ.
3. **NetworkIdle**: thêm `WaitUntil.NetworkIdle`; đếm inflight từ `Network.requestWillBeSent` →
   `loadingFinished`/`loadingFailed`; idle khi 0 request trong 500 ms. `Network.enable` chỉ bật
   trong lúc chờ và tắt lại sau nếu không còn observer/route — giữ nguyên chính sách "chỉ bật
   Network khi caller cần". Goto/Reload/WaitForLoadState đều nhận giá trị mới.
4. **`page.Keyboard` / `page.Mouse`** công khai, bọc `InputDispatcher`/`Keyboard` sẵn có:
   `Press`, `Down`, `Up`, `Type`, `InsertText`; `Move`, `Down`, `Up`, `Click`, `Wheel`.
5. **Route nâng cao**: `FallbackAsync()` (chuyển cho route kế tiếp, rồi tới mạng), `ContinueAsync`
   nhận override `headers`/`method`/`postData`, `FulfillAsync(byte[])` và `FulfillAsync(path)`.
   Vẫn giữ quy tắc "route mới nhất thắng trong từng scope" và auto-continue khi handler không act.
6. **Context helpers**: `SetExtraHttpHeadersAsync`, `SetOfflineAsync`, `SetGeolocationAsync`,
   `EmulateMediaAsync(media, reducedMotion, forcedColors)` áp cho mọi page hiện có và page tạo sau.

**Gate P3b**: test overlay che button → click retry rồi timeout; element dịch chuyển → chờ box ổn
định; `networkidle` với fetch trễ 300 ms; `keyboard`/`mouse` tạo sự kiện `isTrusted`; route
`fallback` rơi đúng thứ tự; fulfill `byte[]` và path. Dự kiến **~174 → ~186 test**.

### 11.4 P3c — Patchright parity cho script trong page và attach

1. **`ExposeFunctionAsync` / `ExposeBindingAsync`** qua `Runtime.addBinding` + event
   `Runtime.bindingCalled`, **không** `Runtime.enable`.
   - **Spike bắt buộc trước khi code**: xác minh `Runtime.bindingCalled` có được gửi khi Runtime
     domain chưa enable. Nếu không, giới hạn còn lại là binding chỉ dùng được từ isolated world
     (vẫn đúng tinh thần Patchright) và ghi rõ trong README, thay vì bật `Runtime.enable`.
   - Kết quả trả về page qua `Runtime.callFunctionOn` trên `objectId` sẵn có.
2. **`AddInitScriptAsync(script)` theo đúng cách Patchright** ("Init Script Shenanigans"): không
   dùng `Page.addScriptToEvaluateOnNewDocument`; thay vào đó chặn request document bằng `Fetch`,
   đọc body gốc (`Fetch.getResponseBody`), chèn `<script>` ngay sau `<head>` (hoặc trước script
   đầu tiên), rồi `Fetch.fulfillRequest` với status/headers gốc.
   - Route chỉ được cài khi thực sự có init script → zero chi phí khi không dùng.
   - Ghi rõ tradeoff ở README: timing attack (Patchright cũng thừa nhận), CSP có thể chặn inline
     script, redirect và response không phải HTML bị bỏ qua.
   - Test trace harness hiện có phải chứng minh **không** xuất hiện `Page.addScriptToEvaluateOnNewDocument`.
3. **`ConnectOverCdpAsync(wsEndpoint)`**: dùng `WebSocketTransport` đã có + `Target.setAutoAttach`,
   trả `CdpBrowser`; cho phép lái Chrome thật/persistent profile — đúng best practice của Patchright
   (`channel: chrome`, `headless: false`, `viewport: null`, không override UA).
4. **Stealth nhỏ**: khi `BrowserContextOptions.ProxyServer` được set, thêm
   `--webrtc-ip-handling-policy=disable_non_proxied_udp` (khuyến nghị của Patchright để WebRTC không
   lộ IP thật). Vẫn không override UA/WebGL/canvas.

**Gate P3c**: `exposeFunction` round-trip (page gọi → C# trả về), binding gọi được từ main world,
init script chạy **trước** script của page trên hai navigation liên tiếp, trace không có lệnh inject,
`connectOverCDP` attach vào browser do test tự spawn ở port-mode rồi điều khiển page. Dự kiến
**~186 → ~196 test**.

### 11.5 Thứ tự & phụ thuộc

`P3a → P3b → P3c`. P3c dựa trên route (P1/P2) và `WebSocketTransport` (M1) nên không cần hạ tầng mới.
Không batch nào được phép nới gate T0: dispatch vẫn **0 B/op**, không `Runtime.enable`/`Console.enable` trong đường thường (ngoại lệ opt-in duy nhất là `routeWebSocket`),
không `Page.addScriptToEvaluateOnNewDocument`, không `sourceURL`.

### 11.6 Rủi ro mới

| # | Rủi ro | Mức | Giảm thiểu |
|---|---|---|---|
| R7 | `Runtime.bindingCalled` không tới khi Runtime chưa enable | Trung bình | Spike 30 phút trước P3c; fallback chỉ expose trong isolated world, không bật domain |
| R8 | `DOM.performSearch` dùng `searchId` toàn cục, rò khi có exception | Thấp | `discardSearchResults` trong `finally`, test hai XPath locator song song |
| R9 | Init script qua route vỡ site có CSP/redirect hoặc response nén | Trung bình | Chỉ bật khi opt-in, test CSP + redirect, ghi rõ giới hạn ở README |
| R10 | Hit-target thêm round-trip mỗi click | Thấp | Có cờ tắt; bỏ qua trong closed shadow; đo lại benchmark click |
| R11 | XPath interpreter phình to, khó chứng minh đúng | Trung bình | Chỉ nhận subset đã liệt kê; trục ngoài subset ném lỗi tường minh; bảng test theo từng predicate |


### 11.7 Kết quả triển khai P3

Khác biệt so với kế hoạch ban đầu ở §11.2–§11.4, và lý do:

- **XPath không dùng `DOM.performSearch`.** `performSearch` tìm trong cả page nên không scope được vào
  frame, và vẫn không thấy closed shadow root. Vì vậy chỉ có một đường: `XPathSelector` chạy trên
  snapshot `DOM.getDocument(pierce:true)` đã có sẵn. Đổi lại tốc độ chậm hơn fast path CSS, nhưng
  đúng ngữ nghĩa (frame + closed shadow + document order) và không cần thêm lệnh CDP nào.
- **`And()` giao theo `backendNodeId`.** Resolve vế phải gọi `DOM.getDocument`, làm node id frontend
  của vế trái hết hiệu lực. Backend id sống qua lần gọi đó, nên giao trên backend rồi trả id mới.
- **`:has-text()` / `:visible` buộc đi đường managed.** Chrome từ chối hai pseudo này trong
  `querySelectorAll`, nên `SelectorPlan.ManagedOnly` bỏ fast path. `:visible` chỉ là approximation
  từ markup (không có layout trong snapshot) và đã ghi rõ.
- **Hit-target dùng `elementFromPoint` trong isolated world**, không dùng `DOM.getNodeForLocation`
  (thêm round-trip và vẫn mù bên trong closed shadow). Target nằm trong closed shadow được chấp
  nhận khi hit là host — giới hạn có chủ đích, đã ghi trong README.
- **Box ổn định lấy mẫu hai lần cách 16 ms** thay vì `requestAnimationFrame`, để không phải chạy
  script trong main world.
- **Context helper là per-page.** `Network.setExtraHTTPHeaders` và
  `Network.emulateNetworkConditions` là lệnh page-target; Chromium không nhận `browserContextId`
  cho chúng. Helper duyệt các page hiện có và bật `Network.enable` trên từng page; page tạo sau
  dùng option trong `BrowserContextOptions`.
- **ExposeFunction chỉ sống trong document hiện tại.** Binding dùng `Runtime.addBinding` (không
  `Runtime.enable`) và wrapper nằm ở main world; navigation xoá wrapper, nên phải gọi lại hoặc kết
  hợp `AddInitScriptAsync`.
- **AddInitScript dùng response stage của Fetch.** `Fetch.enable` thêm pattern
  `{resourceType: Document, requestStage: Response}` khi có init script; handler gọi
  `Fetch.getResponseBody`, chèn `<script>` ngay sau `<head>` (hoặc sau doctype nếu không có head),
  rồi `Fetch.fulfillRequest` với status/headers gốc (bỏ `content-length`, `content-encoding`,
  `transfer-encoding`). Không có `Page.addScriptToEvaluateOnNewDocument`.
- **`connectOverCDP` trả `CdpBrowser` "attached".** Dispose chỉ ngắt kết nối và không đóng tab của
  browser từ xa; `LaunchOptions.ProxyServer`/`ProxyBypassList` được thêm để flag
  `--webrtc-ip-handling-policy=disable_non_proxied_udp` có ngữ cảnh sử dụng thật.
- **Test Chromium bắt buộc:** `scripts/test-chromium.sh` đặt `ZEROCDP_REQUIRE_BROWSER=1`; thiếu
  browser là fail chứ không silent-skip. Toàn bộ **222 test** chạy thật trên chromium (P6 bổ sung 11 test).

---

## 12. P6 — phần parity bổ sung (đã hoàn tất)

Mục tiêu của P6 là đóng các khoảng trống còn lại đã liệt kê sau P4/P5 mà không phá vỡ giao kèo
stealth mặc định. Toàn bộ API mới là additive; không có reflection, serializer hay dependency mới.

### 12.1 API và hành vi đã thêm

- **Web-first assertions**: `LocatorExpect`, `PageExpect`, `CdpAssertionException`, timeout/retry
  riêng. Hỗ trợ attached/visible/hidden/enabled/disabled/editable/checked/focused/empty/in-viewport,
  count, text/contain-text, value/values, attribute, class, id, CSS, role, accessible name/description,
  ARIA snapshot và screenshot assertion. Screenshot assertion trả sai lệch kích thước/pixel theo
  ngưỡng cấu hình.
- **JSHandle/ElementHandle**: `EvaluateHandleAsync` ở page/frame, `QuerySelectorAsync`,
  `QuerySelectorAllAsync`, thuộc tính và thao tác element, `AsElementAsync`, `JsonValueAsync`,
  `GetPropertiesAsync`, `EvaluateHandleAsync`, `DisposeAsync` giải phóng `objectId`.
- **APIRequestContext**: `APIRequest.NewContextAsync`, các verb GET/POST/PUT/PATCH/DELETE/HEAD,
  headers/query/form/multipart/JSON, Basic auth, base URL, proxy, ignore-HTTPS, timeout,
  `FailOnStatusCode` và `APIRequestException`. `context.APIRequest`/`page.APIRequest` là client gắn
  với browser context: gửi cookie từ jar của context và ghi `Set-Cookie` trở lại jar, tương đương
  mô hình `context.request` của Playwright.
- **Coverage**: `Page.Coverage.StartJSCoverageAsync/StopJSCoverageAsync` dùng `Profiler` và
  `StartCSSCoverageAsync/StopCSSCoverageAsync` dùng `CSS`, không cần `Debugger.enable`.
- **Context option parity**: `BaseUrl`, `IgnoreHttpsErrors`, `Permissions`, `AcceptDownloads`,
  `HasTouch`, `Offline`, `Screen`, `ReducedMotion`, `ForcedColors`, `Contrast`, `ServiceWorkers`,
  `HttpCredentials`. HTTP auth dùng `Fetch.enable(handleAuthRequests: true)` và
  `Fetch.continueWithAuth`, đã có e2e Basic auth.
- **Option parity**: check/uncheck/select/PDF/script/style/screenshot options, mask/animations/caret/
  scale/style, `PressSequentially`, `SetChecked`, `PdfAsync(PdfOptions)` và legacy overloads.
- **`RouteWebSocketAsync`**: mock/proxy frame, binary frame, close, context route áp dụng cho page
  hiện tại lẫn page tạo sau, `UnrouteWebSocketAsync`. URL khớp theo glob, page route ưu tiên context
  route.
- **Trace archive**: `TraceArchive.Open` đọc ZIP hoặc thư mục đã giải nén; parse `trace.trace`,
  `trace.network`, screencast frame và source; cung cấp `Actions`, `Network`, `ScreencastFrames`,
  `Sources`, `Warnings`; `WriteViewerAsync` tạo HTML self-contained. Đây là reader/schema/viewer
  mức thư viện, không phải React trace viewer của Playwright.

### 12.2 Quyết định kỹ thuật

- **RouteWebSocket không thể dùng `Runtime.addBinding` cho document mới.** Thực nghiệm trên
  Chromium 155 cho thấy binding thêm bằng `Runtime.addBinding` không tồn tại trong main world của
  document được tạo sau. Wrapper vì vậy gửi frame qua `console.log` đã capture native và
  `Runtime.enable` + `Runtime.consoleAPICalled` (chỉ khi route được đăng ký). Đây là opt-in có chủ
  đích, không phải đường stealth mặc định.
- **Handler chạy trước khi page nhận `open`.** Handler được start đồng bộ để đăng ký `OnMessage`
  trước khi `accept` được gửi; handler async không làm trễ open. Với socket proxy, page chỉ nhận
  `open` sau khi handshake tới server thật hoàn tất, nên `onopen` -> `send()` không thể rơi vào
  giai đoạn native còn CONNECTING. Message đến trước open được xếp hàng trong wrapper.
- **Wrapper được chèn bằng document transformer của `AddInitScriptAsync`.** Không dùng
  `Page.addScriptToEvaluateOnNewDocument`; CSP nghiêm ngặt có thể chặn wrapper và đây là giới hạn
  đã ghi trong README.
- **Không mở rộng `ConsoleMessage`/`PageError`/`WebError`** thành sự kiện page thường vì cần
  `Runtime.enable`/`Console.enable`. Debugger/pause vẫn ngoài phạm vi.

### 12.3 Gate

- `dotnet build ZeroCDP.slnx -c Release`: 0 warning, 0 error.
- Chromium suite: **222/222 test pass**; T0 dispatch vẫn **0 B/op**.
- Các test mới bao phủ assertions, handles, APIRequestContext (kể cả cookie sharing hai chiều),
  coverage, Basic auth, option parity, mock/proxy/context WebSocket route và trace archive ZIP/thư mục.
