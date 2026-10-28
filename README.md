# ZeroCDP

[English](README.md) · [Tiếng Việt](README.vi.md)

A Chrome DevTools Protocol client library for .NET 10 with a zero-allocation dispatch core,
NativeAOT support, and a Patchright-style stealth posture implemented from scratch — no runtime
reflection, no serializers, no `dynamic`.

ZeroCDP talks to a Chromium-family browser (Chrome, Chromium, Edge) over
`--remote-debugging-pipe` by default on Linux, macOS and Windows, so no debugger TCP port is opened.
POSIX uses child fds 3/4; Windows passes two inherited anonymous-pipe handles through
`--remote-debugging-io-pipes`. `CdpTransportKind.Port` remains available for callers that need a
loopback WebSocket endpoint. JavaScript evaluation uses an isolated world and never enables the
Runtime domain. The only opt-in Runtime use is the `RouteWebSocketAsync` console transport, which is
documented separately.

The current package is [**ZeroCdp** on NuGet](https://www.nuget.org/packages/ZeroCdp) at
**0.1.0-alpha**. The API is usable for evaluation, but minor releases may still make additive or
corrective changes.

## Installation

```bash
dotnet add package ZeroCdp
```

Or set the version explicitly:

```bash
dotnet add package ZeroCdp --version 0.1.0-alpha
```

The package targets `net10.0`, is fully trimmable (`IsTrimmable`, `IsAotCompatible` analyzers are
enforced at build time) and depends only on the Base Class Library.

## Status

| Milestone | State | What it means |
|---|---|---|
| M0 | Done | Skeleton, AOT analyzers, NativeAOT smoke publish |
| M1 | Done | Pipe transport (POSIX fd 3/4, Windows inherited handles), NUL framing, command table, reader loop |
| M2 | Done | Flattened sessions, target auto-attach, session demultiplexing |
| M3 | Done | Navigation, lifecycle waits, title/url/content, cookies |
| M4 | Done | CSS engine, locators, real input events, closed shadow roots |
| M5 | Done | Isolated worlds, no `Runtime.enable`, launch profiles, trace harness |
| M6 | Done | Redaction, resource limits, command timeout, navigation allow-list |
| M7 | Done | Benchmarks, packaging, docs, AOT sample |
| P0 | Done | Screenshots, dialogs, same-process iframes, locator auto-wait, fill/check/hover/select/press |
| P1 | Done | Browser contexts, network routing/observation, navigation history, semantic locators, locator state/box/focus, uploads, permissions, element screenshots, Patchright refinements |
| P2 | Done | Popups, downloads, file chooser, storage state, context routes/events, frame closed-shadow + semantic locators, locator editing/evaluation/drag, wait helpers |
| P3 | Done | Selector parity, XPath, hit-target/stable-box actionability, NetworkIdle, keyboard/mouse, context network helpers, bindings, init scripts, connectOverCDP |
| P4 | Done | Out-of-process iframes (including nested), child-session routing, synthetic node ids, composed coordinate transforms, Playwright-style options/enums, runnable real-world sample |
| P5 | In progress | HAR record/replay, ARIA snapshots, Clock, IndexedDB storage state, continuous/chunked tracing, DOM snapshots and source maps, video |
| P6 | Done | Web-first assertions, JS/Element handles, standalone and context-bound APIRequestContext, JS/CSS coverage, context option parity, HTTP auth, WebSocket routing, trace archive reader and viewer |

The T0 gate is a hard assertion: dispatching a CDP event allocates **0 bytes** on the steady-state
path, including session-scoped routing. On the development machine a 150-byte event frame dispatches
in roughly 234 ns, and a session-routed event in roughly 258 ns. The full suite is **222 tests**.

## Requirements

- .NET SDK 10.0
- A Chromium-family browser already installed. ZeroCDP never downloads a browser.
  Set `ZEROCDP_CHROME_PATH` to override discovery.

## Quick start

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

// JavaScript runs in an isolated world. The page's globals are not visible, the DOM is shared,
// and the Runtime domain is never enabled.
EvaluationResult result = await page.EvaluateAsync("document.querySelectorAll('a').length");
if (result.Value.TryGetInt64(out long links))
{
    Console.WriteLine($"{links} links");
}
```

### Install from NuGet

```bash
dotnet add package ZeroCdp --version 0.1.0-alpha
```

Locators re-resolve on every action, so they keep working after navigation or a re-render. Actions
poll for up to 30 seconds (configurable through `CdpPage.DefaultTimeout` or a per-locator timeout)
until the element is attached, visible and enabled; text entry also requires an editable field.
Normal locators transparently fall back to a pierced `DOM.getDocument` snapshot when the browser's
normal query finds nothing, so **closed** shadow roots are reachable without a flag, matching
Patchright. The fallback is throttled to 200 ms so a missing selector does not snapshot the document
on every poll. The explicit `pierceShadowDom: true` path remains available:

```csharp
Locator inner = page.Locator("#inner", pierceShadowDom: true);
await inner.ClickAsync();
```

### Playwright-style advanced API

The native API remains available, and the advanced surface also accepts Playwright-style options
objects, enums and spellings. These overloads are additive; existing code does not need to change.

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

// Web-first assertions retry until the condition is true or the timeout expires.
await page.Expect().ToHaveTitleAsync("Checkout");
await page.Locator("#save").Expect().ToBeVisibleAsync();
await page.Locator(".row").Expect().ToHaveCountAsync(3);
await page.Locator("#email").Expect().ToHaveValueAsync("ada@example.com");

// Object handles keep a remote JS object alive across calls.
await using JSHandle config = await page.EvaluateHandleAsync("window.appConfig");
await using ElementHandle save = await page.QuerySelectorAsync("#save");
Assert.NotNull(save);
await save!.ClickAsync();
string? state = await save.GetAttributeAsync("data-state");

// A standalone HTTP client with Playwright-style options; it does not need a browser page.
await using APIRequestContext api = await APIRequest.NewContextAsync(new APIRequestNewContextOptions
{
    BaseUrl = "https://api.example.test",
    ExtraHttpHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" },
    Timeout = TimeSpan.FromSeconds(10),
});
await using APIResponse apiResponse = await api.GetAsync("/users/1");
JsonElement? json = await apiResponse.JsonAsync();

// Or use the request client owned by a browser context. It shares the context cookie jar in both
// directions: API responses update browser cookies, and browser cookies are sent by API requests.
// page.APIRequest returns the same client as page.Context.APIRequest.
await using APIResponse contextResponse = await context.APIRequest.GetAsync("/account");
Assert.Same(context.APIRequest, page.APIRequest);
string account = await contextResponse.TextAsync();

// Coverage is opt-in and uses the Profiler/CSS domains without Debugger.enable.
await page.Coverage.StartJSCoverageAsync();
await page.Coverage.StartCSSCoverageAsync();
await page.GotoAsync("https://example.com");
IReadOnlyList<JSCoverageEntry> jsCoverage = await page.Coverage.StopJSCoverageAsync();
IReadOnlyList<CssCoverageEntry> cssCoverage = await page.Coverage.StopCSSCoverageAsync();
```

`WaitForURLAsync` is an extension method because a case-only member next to the native
`WaitForUrlAsync` would violate CA1708. The native `WaitUntil`, `WaitForState` and
`ScreenshotOptions` types remain the implementation; the Playwright types are thin adapters.

### Action surface

The P0–P4 surface covers the actions an automation script usually needs:

```csharp
await page.Locator("#name").FillAsync("Ada");
await page.Locator("#terms").CheckAsync();
await page.Locator("#plan").SelectOptionAsync("pro");
await page.Locator("#name").PressAsync("Enter");
await page.Locator("#details").WaitForAsync(WaitForState.Visible, TimeSpan.FromSeconds(5));

// Playwright-style semantic locators (pierced snapshot, so closed roots work too).
await page.GetByRole("button", "Sign in").ClickAsync();
await page.GetByLabel("Email address").FillAsync("ada@example.com");
await page.GetByPlaceholder("Search").FillAsync("cdp");
await page.GetByText("Welcome back").WaitForAsync(WaitForState.Visible);
await page.GetByTestId("row-1").ClickAsync();
await page.GetByAltText("Company logo").ClickAsync();
await page.GetByTitle("Close dialog").ClickAsync();

// Playwright-style indexing and reads.
await page.Locator(".row").First().ClickAsync();
await page.Locator(".row").Nth(2).ClickAsync();
string[] texts = await page.Locator(".row").AllTextContentsAsync();
string[] renderedRows = await page.Locator(".row").AllInnerTextsAsync();
string? href = await page.Locator("a.primary").GetAttributeAsync("href");
string value = await page.Locator("#name").InputValueAsync() ?? string.Empty;
string rendered = await page.Locator("#summary").InnerTextAsync();

// Box, focus and state checks.
BoundingBox? box = await page.Locator("#name").BoundingBoxAsync();
await page.Locator("#name").FocusAsync();
bool enabled = await page.Locator("#save").IsEnabledAsync();
bool editable = await page.Locator("#name").IsEditableAsync();
bool checkedNow = await page.Locator("#terms").IsCheckedAsync();
bool hidden = await page.Locator("#banner").IsHiddenAsync();

// Editing, evaluation, events and drag.
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

// Trusted mouse options, uploads and element screenshots.
await page.Locator("#menu").ClickAsync(new ClickOptions { Button = MouseButton.Right });
await page.Locator("#upload").SetInputFilesAsync("/tmp/report.pdf");
byte[] cardPng = await page.Locator(".card").ScreenshotBytesAsync();

await page.ScreenshotAsync("page.png", new ScreenshotOptions { FullPage = true });

FrameLocator checkout = page.FrameLocator("iframe#checkout");
await checkout.Locator("button.pay").ClickAsync();
await checkout.GetByRole("button", "Apply coupon").ClickAsync();
await checkout.GetByLabel("Postal code").FillAsync("94107");

// Cross-site iframes are out-of-process. ZeroCDP auto-attaches the child target per page,
// routes DOM/JS work through its session, and composes the iframe's affine transform so
// clicks and BoundingBoxAsync use main-page viewport coordinates. Nested OOPIFs work too.
FrameLocator nested = page.FrameLocator("iframe#outer").Frame("iframe#inner");
await nested.GetByRole("button", "Continue").ClickAsync();

// Popups, downloads and file choosers are first-class page events.
CdpPage popup = await page.RunAndWaitForPopupAsync(
    () => page.Locator("#open-popup").ClickAsync());
Download download = await page.RunAndWaitForDownloadAsync(
    () => page.Locator("#export").ClickAsync());
await download.WaitForCompletionAsync();
await download.SaveAsAsync("/tmp/export.csv");

FileChooser chooser = await page.RunAndWaitForFileChooserAsync(
    () => page.Locator("#pick-file").ClickAsync());
await chooser.AcceptAsync("/tmp/report.pdf");

// Dialogs auto-dismiss unless a subscriber is attached. With one attached, the dialog stays
// open until AcceptDialogAsync/DismissDialogAsync is called.
page.AutoDismissDialogs = false;
page.DialogOpening += dialog => Console.WriteLine($"{dialog.Type}: {dialog.Message}");

// Call from your flow after the action that opens the dialog; returns false when none is open.
await page.AcceptDialogAsync();
```

### Contexts, storage, tracing, video

Isolated browser contexts isolate cookies, cache and permissions. Context options are applied before
the first navigation. `context.APIRequest` (also exposed as `page.APIRequest`) shares the context
cookie jar: API responses update browser cookies and API requests send existing browser cookies.

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
await context.UnrouteAllAsync(); // HTTP and WebSocket routes

// Playwright-compatible storage state, including localStorage for HTTP(S) origins.
StorageState state = await context.StorageStateAsync();
await state.SaveAsync("/tmp/state.json");
StorageState loaded = await StorageState.LoadAsync("/tmp/state.json");
await otherContext.SetStorageStateAsync(loaded);

// IndexedDB is opt-in and uses Playwright's value/key + valueEncoded/keyEncoded wire shape.
StorageState fullState = await context.StorageStateAsync(
    new StorageStateOptions { IndexedDb = true });

// Accessibility/ARIA snapshot, fake clock and HAR replay.
string aria = await page.Locator("main").AriaSnapshotAsync();
await page.Clock.InstallAsync();
await context.RouteFromHARAsync("/tmp/session.har");

// Trace recording plus per-page video. Video encoding requires ffmpeg; FfmpegPath can point at
// an ffmpeg binary shipped beside the application.
await using BrowserContext recorded = await browser.Browser.NewContextAsync(new BrowserContextOptions
{
    RecordVideo = new RecordVideoOptions { Dir = "/tmp/videos" },
});
await recorded.Tracing.StartAsync(new TracingStartOptions
{
    Title = "checkout",
    Screenshots = true, // continuous screencast frames
    Snapshots = true,   // MHTML snapshots on load and at chunk boundaries
    Sources = true,     // JS, CSS and source-map resources
});
CdpPage videoPage = await recorded.NewPageAsync("https://example.com");
await recorded.Tracing.StopChunkAsync("/tmp/checkout-1.zip");
await videoPage.GotoAsync("https://example.com/next");
await recorded.Tracing.StopAsync("/tmp/checkout-2.zip");
await videoPage.CloseAsync();
string videoPath = await videoPage.Video!.PathAsync();

// Read a recorded archive without a browser and generate a self-contained viewer.
using TraceArchive trace = TraceArchive.Open("/tmp/checkout-2.zip");
TraceContextOptions? contextInfo = trace.Context;
IReadOnlyList<TraceAction> actions = trace.Actions;
IReadOnlyList<TraceNetworkEntry> requests = trace.Network;
byte[] firstFrame = trace.ReadEntryBytes(trace.ScreencastFrames[0].File);
await trace.WriteViewerAsync("/tmp/checkout-viewer.html");
```

### Network interception

Network interception uses `Fetch.enable` only while at least one route exists. Observation and
waiters enable `Network.enable`; those paths do not enable `Runtime.enable` or `Console.enable`.
`RouteWebSocketAsync` is a separate opt-in that enables Runtime for its console transport:

```csharp
await page.RouteAsync("**/api/users", async route =>
{
    await route.FulfillAsync(
        200,
        """[{"id":1,"name":"Ada"}]""",
        "application/json; charset=utf-8");
});

// Context routes cover existing pages and pages created later, including their first
// navigation. Page routes take priority over context routes.
await context.RouteAsync("**/api/**", async route =>
{
    await route.FulfillAsync(200, """{"cached":true}""", "application/json");
});

// Continue to the network with a rewritten URL, method, body and headers. The original Host and
// other headers are merged; stale body-length headers are removed so Chromium resumes correctly.
await page.RouteAsync("**/legacy/**", route => route.ContinueAsync(
    "https://example.test/v2/items",
    method: "POST",
    postData: """{"migrated":true}""",
    headers: [new CdpHeader("content-type", "application/json")]));

// WebSocket routing is opt-in. A matched handler is a local mock; call ConnectToServerAsync to
// proxy to the real endpoint. Once a page has any route, the wrapper covers every socket on that
// page; unmatched sockets are proxied transparently. Without a registration nothing is injected.
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

// Waits can also wrap the action that triggers them.
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

Navigation helpers include `ReloadAsync`, `GoBackAsync`, `GoForwardAsync`,
`GetNavigationHistoryAsync`, `WaitForUrlAsync`, `WaitForNavigationAsync`, `WaitForLoadStateAsync`,
`SetContentAsync`, `SetViewportSizeAsync` with the `ViewportSize` property,
`EmulateColorSchemeAsync`, `PdfAsync`, `BringToFrontAsync`,
`SetBypassCspAsync`, `SetGeolocationAsync`, offline/cache header controls, and response-body reads.
`RunAndWaitForRequestAsync`, `RunAndWaitForResponseAsync`, `RunAndWaitForRequestFinishedAsync`,
`RunAndWaitForRequestFailedAsync` and `RunAndWaitForWebSocketAsync` follow Playwright's naming on
both page and context. Page, frame and handle surfaces also expose the selector read/state helpers
(`GetAttributeAsync`, `InnerHTMLAsync`, `InnerTextAsync`, `TextContentAsync`, `InputValueAsync`,
`IsVisibleAsync`, `IsHiddenAsync`, `IsCheckedAsync`, `IsEnabledAsync`, `IsDisabledAsync`,
`IsEditableAsync`) and the handle actions (`TapAsync`, `SelectTextAsync`, `DispatchEventAsync`,
`CheckAsync`/`UncheckAsync`/`SetCheckedAsync` with options). API responses expose `HeadersAsync`
and `HeadersArrayAsync`, and `APIRequestContext.StorageStateAsync` captures cookies.

## Stealth model

ZeroCDP follows one rule: *do not add anything to the page that the page can see*.

| Leak | ZeroCDP behaviour |
|---|---|
| `Runtime.enable` | Not sent by ordinary page or stealth flows. Evaluation uses `Page.createIsolatedWorld` + an explicit `executionContextId`, including inside frames. Main-world `Runtime.evaluate` is an explicit opt-in and still never enables the domain. `RouteWebSocketAsync` is the one opt-in exception: it enables Runtime to receive the wrapper's console transport. |
| `Console.enable` | Never sent. |
| `Target.setDiscoverTargets` | Never sent. Only flattened `Target.setAutoAttach` is used at the browser and page sessions (the latter is required for OOPIFs). |
| Injected scripts | None by default: no `Page.addScriptToEvaluateOnNewDocument`, no `sourceURL`. `AddInitScriptAsync` uses a Fetch response transform; `RouteWebSocketAsync` injects a WebSocket wrapper. Both are explicit opt-ins. |
| Network domains | `Network.enable` is sent only for explicit observation or waiters. `Fetch.enable` exists only while at least one route is registered. Routes auto-continue or abort rather than hanging a paused request. |
| Automation flags | `--enable-automation` is never passed. `StealthProfile.Default` adds `--disable-blink-features=AutomationControlled` and omits `--disable-gpu`. |
| World names | 32 random hex characters, unique per page and carrying no library identity. |
| Closed shadow roots | Normal locators use Chrome's fast query first, then a throttled pierced `DOM.getDocument` snapshot, matching Patchright without injecting JavaScript. |

`StealthProfile` values:

- `Raw` — neutral baseline; useful for comparison. `navigator.webdriver` may be true.
- `Minimal` — disables `AutomationControlled`, keeps conventional headless flags.
- `Default` — `Minimal` plus no `--disable-gpu` headless fingerprint. This is the default.

Evaluation is isolated by default. `page.EvaluateAsync("window.app", isolatedContext: false)` runs in the
page's main world for cases that genuinely need page globals; this is the Patchright-style escape hatch,
not the default. When `Network.enable` is used for routing/observation, the library never turns on
`Runtime.enable` or `Console.enable` as a side effect. The only exception is an explicit
`RouteWebSocketAsync` registration, which opts into `Runtime.enable` and a main-world wrapper; a strict
Content-Security-Policy can block that wrapper.

The repository contains a trace harness that records every outbound command and asserts that
`Runtime.enable`, `Console.enable`, `Debugger.enable`, `Target.setDiscoverTargets` and script
injection commands never appear in ordinary page, network and stealth flows. Tests for the opt-in
`RouteWebSocketAsync` path expect `Runtime.enable`; no test treats that command as stealth-safe. A network-gated check against bot.sannysoft.com can be run with
`ZEROCDP_E2E_NETWORK=1`.

Stealth is an arms race, not a guarantee. ZeroCDP aims for browser behaviour that looks like real
use; it does not promise to beat every detector.

## Security

- **No debugger port** by default on every supported platform. Linux/macOS use child fds 3/4;
  Windows uses inherited anonymous-pipe handles passed with `--remote-debugging-io-pipes`. There is
  no listening socket in pipe mode. Port mode binds `127.0.0.1` only.
- **Private profile** per launch, owner-only permissions on Unix, deleted on dispose unless
  `KeepProfile` or an explicit `UserDataDirectory` is set.
- **Sandbox on** by default. `DisableSandbox = true` is explicit and emits a trace warning.
- **Resource limits**: `MaxFrameBytes` (32 MB), `MaxPendingCommands` (4096), `MaxDomDepth` (512)
  and a shared-wheel `CommandTimeout` (30 s) that costs no allocation per command. Full-page
  screenshots are capped at 16,384 px per dimension and 64 million pixels.
- **`LogRedactor`** masks passwords, cookies, authorization headers, tokens,
  `Input.insertText.value` and `Page.handleJavaScriptDialog.promptText` before JSON is logged.
  Malformed input is replaced, never logged raw.
- **`NavigationPolicy`** is an origin allow-list checked before `Page.navigate`:
  ```csharp
  await using var browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
  {
      NavigationPolicy = new NavigationPolicy("https://example.com", "https://api.example.com"),
  });
  ```
- No certificate-error bypass is ever enabled by the library.

## Architecture

```
src/ZeroCdp/
├─ Transport/   pipe (fd 3/4 on POSIX, inherited handles on Windows), loopback WebSocket
├─ Runtime/     connection, dispatcher, pending table, session routing
├─ Protocol/    hand-written UTF-8 command writers and result readers
├─ Browser/     launcher, browser, contexts, page/locator partials, frames,
│               options/enums, screenshots, dialogs, input, network state/routing,
│               downloads, file choosers, storage state, tracing, trace reader/viewer,
│               API requests, coverage, handles, WebSockets, video
├─ Stealth/     isolated world runtime, world-name factory
├─ Security/    navigation policy, log redaction
├─ Dom/         allocation-light DOM snapshot tree
└─ Selectors/   CSS parser and matcher for shadow-piercing queries
```

No reflection, no serializer, no `dynamic`, no `JsonDocument` on the hot path. Result structs parse
directly from the receive buffer; spans are only valid for the duration of a callback, so retaining
a value requires an explicit copy.

## Build and test

```bash
dotnet build ZeroCDP.slnx -c Release
dotnet test tests/ZeroCdp.Tests/ZeroCdp.Tests.csproj -c Release
dotnet run --project tests/ZeroCdp.Benchmarks -c Release
dotnet run --project samples/ZeroCdp.Sample.RealWorld -c Release
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release
dotnet publish samples/ZeroCdp.Sample.AotConsole -c Release -r linux-x64
```

The real-world sample starts its own HTTP server on a random port, uses a private browser profile
and can run at the same time as the test suite.

`ZeroCdp.Sample.BotCheck` opens a **headful** browser with a persistent profile and leaves the
CDP session attached while you navigate manually. Its start page links to bot.sannysoft.com,
rebrowser-bot-detector, CreepJS, BrowserScan and Pixelscan. Close the browser window or press
Ctrl+C to end the session. Useful options:

```bash
# Open a specific page immediately.
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release -- \
  --start https://bot.sannysoft.com/

# Use a different persistent profile or launch profile.
dotnet run --project samples/ZeroCdp.Sample.BotCheck -c Release -- \
  --user-data-dir /tmp/zerocdp-botcheck --stealth default
```

The default profile is `~/.zerocdp/botcheck-profile`, so cookies and site storage survive between
runs. The optional `--headless` flag exists only for smoke tests; the normal mode is a visible
window for manual inspection.

### Packaging

```bash
dotnet pack src/ZeroCdp/ZeroCdp.csproj -c Release
```

The package is produced under `artifacts/` and contains the README alongside the library binaries.

### Benchmarks

Representative Release run on linux-x64: dispatch **~234 ns** for a 150-byte event and **0 B/op** over
2,000,000 dispatches; session-routed dispatch **~258 ns** and **0 B/op**; isolated evaluate **~339 µs**;
selector query **~805 µs**; closed-shadow lookup **~573 µs**; automatic closed-shadow lookup **~650 µs**;
semantic role lookup **~550 µs**; locator click **~33 ms**; `Browser.getVersion` **~133 µs**.
The AOT sample publishes to **8.0 MiB** and the package to about **450 KB**.

The test suite tolerates a machine without a browser: environment-dependent tests return early
instead of failing. On a machine with Chromium the **222-test** suite exercises the real pipe transport,
isolated worlds, contexts, page/context routes and observation, response bodies, navigation history,
CSS and semantic locators, same-process and out-of-process frames (including nested OOPIFs),
closed-shadow piercing, storage state (including IndexedDB), popups, downloads, file choosers, uploads,
permissions/geolocation, screenshots, dialogs, assertions, handles, API requests (including context cookie sharing), coverage, HTTP auth,
WebSocket routing, trace archives/viewer, video and security policy.

## Limitations

- Chromium-family only. Firefox/WebKit would need WebDriver BiDi.
- Windows pipe mode relies on `Process.Start` inheriting the two anonymous-pipe handles. If a host
  process enforces unusual handle-inheritance restrictions, use `CdpTransportKind.Port` for that
  deployment. The Windows pipe path is not exercised by this Linux development machine.
- `NavigationPolicy` checks the initial navigation. Server-side redirects need explicit request
  interception; route handlers are not automatically constrained by the policy.
- `FrameLocator` supports same-process and out-of-process iframes, including nested OOPIFs, closed
  shadow roots and semantic `GetBy*` locators. Child sessions are auto-attached per page; DOM and JS
  commands are routed to the owning target, while clicks and bounding boxes are mapped back to the
  main viewport. Frames inside a page with an unusual CSS transform are mapped with an affine
  transform of the iframe content box.
- Actionability checks attached, visible, enabled and (for text entry) editable state, plus
  hit-target/occlusion and a two-frame stable-box check. The hit-target check accepts the host when
  the target is inside a closed shadow root, because the hit node cannot be resolved through that
  boundary without injecting script.
- Semantic locators use a managed pierced-DOM snapshot and approximate ARIA name computation for
  common native HTML roles. Complex ARIA relationships and custom role providers are not complete.
- Storage-state export enumerates localStorage for HTTP(S) origins with an open page in the context.
  IndexedDB is opt-in via `StorageStateOptions.IndexedDb` and preserves databases, stores, indexes
  and records using Playwright's JSON plus encoded Date/typed-array/Map/Set wire shape. Import seeds
  origins through a temporary target without hitting the real origin.
- A popup's very first document can load before its target session is registered, so a context
  route may start intercepting only its subsequent requests. Pages created through `NewPageAsync`
  are routed before their first navigation.
- Downloads use one shared temporary directory per browser and are named by Chromium's download
  GUID. `SaveAsAsync` copies the completed file; the original is deleted when the context/browser
  cleans up or `DeleteAsync` is called.
- `DragToAsync` performs a trusted left-button mouse drag with intermediate moves. Custom drag
  interception and drag-and-drop of external files are not implemented.
- WebSocket frames are observed through the Network domain, and `RouteWebSocketAsync` can mock or
  proxy them. Routing installs a main-world `WebSocket` wrapper and enables `Runtime.enable`; this is
  inherently visible to the page and strict CSP can block it. Once a page has a route, every socket
  in that page passes through the wrapper; unmatched sockets are proxied transparently. Without a
  registration there is no wrapper. Permessage-deflate negotiation is left to the browser. HTTP
  routes use `Fetch.enable`; observation uses `Network.enable` only when requested.
- Full-page screenshots are capped at 16,384 pixels per dimension and 64 million pixels total.
  Element screenshots ignore `FullPage` and capture the element's visible bounding box.
- Video recording requires `ffmpeg` on `PATH`, in `ZEROCDP_FFMPEG_PATH`/`FFMPEG_PATH`, or at
  `RecordVideoOptions.FfmpegPath`; each page's WebM is finalized when that page closes.
- Tracing uses one shared `Page.startScreencast` per page, so video and tracing can run together with
  a single frame acknowledgement. Frames are sampled at ~5 fps and written to temporary files before
  being streamed into the zip, keeping memory bounded. Each `StopChunkAsync` archives that chunk's
  events and starts a fresh chunk; `trace.network` is cumulative for the recording session.
- `Snapshots` stores self-contained MHTML at load boundaries and chunk/stop boundaries. This is a
  pragmatic DOM snapshot, not Playwright's layout/ARIA snapshot schema. `Sources` bundles JS, CSS and
  `.map` bodies, follows `sourceMappingURL`, and caps each resource at 4 MiB. `TraceArchive` reads ZIP
  or extracted archives, exposes typed actions/network/frames/sources and writes a self-contained HTML
  viewer. Playwright's React trace-viewer UI, codegen and the test runner remain out of scope.
- `ConsoleMessage`, `PageError`, `WebError` and console events are not exposed as normal page events,
  because they require `Runtime.enable`/`Console.enable`; use `Log.enable` explicitly when needed.
  Debugger pause APIs are likewise outside the Patchright-compatible surface.
- Playwright's test runner, codegen and UI mode are intentionally out of scope for the library.

## License

MIT. See [LICENSE](LICENSE).

The full Vietnamese version of this document is in [README.vi.md](README.vi.md).
