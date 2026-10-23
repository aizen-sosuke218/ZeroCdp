using System.Text;
using System.Text.Json;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>How far a navigation should be allowed to progress before it is considered done.</summary>
public enum WaitUntil
{
    /// <summary>Return as soon as the browser accepts the navigation.</summary>
    Commit = 0,

    /// <summary>Wait for the main frame's <c>DOMContentLoaded</c>.</summary>
    DomContentLoaded = 1,

    /// <summary>Wait for the main frame's <c>load</c> event.</summary>
    Load = 2,

    /// <summary>
    /// Wait for <c>load</c> and then until no request has been in flight for 500 ms. Watching this
    /// state enables the Network domain, because idle cannot be observed without it.
    /// </summary>
    NetworkIdle = 3,
}

/// <summary>
/// Per-page event handling: tracks load progress and forwards every event to the caller's sink.
/// </summary>
/// <remarks>
/// The sink sits between the session and the user so navigation can be observed without asking
/// the caller to wire anything up. Frame ids are compared as UTF-8 bytes, so a busy page does not
/// allocate just because it reports lifecycle events.
/// </remarks>
internal sealed class PageState : ICdpEventSink
{
    private readonly Lock _gate = new();
    private byte[]? _mainFrameUtf8;
    private NavigationWaiter? _pending;

    public ICdpEventSink? UserSink { get; set; }

    /// <summary>Network events are parsed here so page navigation and public events share one sink.</summary>
    public NetworkState? Network { get; set; }

    /// <summary>Called when the main frame commits a new document.</summary>
    public Action? MainFrameNavigated { get; set; }

    /// <summary>Called when the main frame's load event fires.</summary>
    public Action? Load { get; set; }

    /// <summary>Called when the main frame's DOMContentLoaded event fires.</summary>
    public Action? DomContentLoaded { get; set; }

    /// <summary>Called for every frame that commits a document.</summary>
    public Action<FrameTreeEntry>? FrameNavigated { get; set; }

    /// <summary>Called when a child frame is attached.</summary>
    public Action<string, string?>? FrameAttached { get; set; }

    /// <summary>Called when a frame is detached.</summary>
    public Action<string>? FrameDetached { get; set; }

    /// <summary>Called when a frame changes URL without committing a new document.</summary>
    public Action<string, string>? FrameUrlChanged { get; set; }

    /// <summary>Called when the page opens a JavaScript dialog.</summary>
    public Action<DialogInfo>? DialogOpening { get; set; }

    /// <summary>Called when the current JavaScript dialog closes.</summary>
    public Action? DialogClosed { get; set; }

    /// <summary>Called when Chromium starts a download for this page.</summary>
    public Action<DownloadBeginInfo>? DownloadWillBegin { get; set; }

    /// <summary>Called as a download makes progress or finishes.</summary>
    public Action<DownloadProgressInfo>? DownloadProgress { get; set; }

    /// <summary>Called when Chromium intercepts a file chooser opened by this page.</summary>
    public Action<FileChooserInfo>? FileChooserOpened { get; set; }

    /// <summary>
    /// Called when page JavaScript invokes a <c>Runtime.addBinding</c> binding. This event arrives
    /// without enabling the Runtime domain.
    /// </summary>
    public Action<string, string?>? BindingCalled { get; set; }

    /// <summary>
    /// Called for console arguments when the Runtime domain is enabled. Route WebSocket uses this
    /// opt-in channel because Runtime.addBinding is not installed in documents created later.
    /// </summary>
    public Action<string>? ConsoleMessage { get; set; }

    /// <summary>Called for each JPEG frame while a screencast is active.</summary>
    public Action<ScreencastFrameInfo>? ScreencastFrameReceived { get; set; }

    public void SetMainFrame(ReadOnlySpan<byte> frameIdUtf8)
        => _mainFrameUtf8 = frameIdUtf8.ToArray();

    public NavigationWaiter BeginNavigation(WaitUntil waitUntil)
    {
        NavigationWaiter waiter = new(waitUntil);
        NavigationWaiter? previous;
        lock (_gate)
        {
            previous = _pending;
            _pending = waiter;
        }

        previous?.Abandon("A newer navigation started before this one finished.");
        return waiter;
    }

    public void EndNavigation(NavigationWaiter waiter)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, waiter))
            {
                _pending = null;
            }
        }
    }

    public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
    {
        Network?.OnEvent(method, paramsUtf8);

        if (method.SequenceEqual("Page.loadEventFired"u8))
        {
            Signal(SignalKind.Load);
            Load?.Invoke();
        }
        else if (method.SequenceEqual("Page.lifecycleEvent"u8))
        {
            HandleLifecycle(paramsUtf8);
        }
        else if (method.SequenceEqual("Page.frameAttached"u8))
        {
            HandleFrameAttached(paramsUtf8);
        }
        else if (method.SequenceEqual("Page.frameDetached"u8))
        {
            HandleFrameDetached(paramsUtf8);
        }
        else if (method.SequenceEqual("Page.frameNavigated"u8))
        {
            HandleFrameNavigated(paramsUtf8);
        }
        else if (method.SequenceEqual("Page.navigatedWithinDocument"u8))
        {
            HandleSameDocumentNavigation(paramsUtf8);
        }
        else if (method.SequenceEqual("Page.javascriptDialogOpening"u8))
        {
            if (DialogInfo.TryParse(paramsUtf8, out DialogInfo dialog))
            {
                DialogOpening?.Invoke(dialog);
            }
        }
        else if (method.SequenceEqual("Page.javascriptDialogClosed"u8))
        {
            DialogClosed?.Invoke();
        }
        else if (method.SequenceEqual("Page.downloadWillBegin"u8))
        {
            if (DownloadBeginInfo.TryParse(paramsUtf8, out DownloadBeginInfo download))
            {
                DownloadWillBegin?.Invoke(download);
            }
        }
        else if (method.SequenceEqual("Page.downloadProgress"u8))
        {
            if (DownloadProgressInfo.TryParse(paramsUtf8, out DownloadProgressInfo progress))
            {
                DownloadProgress?.Invoke(progress);
            }
        }
        else if (method.SequenceEqual("Page.fileChooserOpened"u8))
        {
            if (FileChooserInfo.TryParse(paramsUtf8, out FileChooserInfo chooser))
            {
                FileChooserOpened?.Invoke(chooser);
            }
        }
        else if (method.SequenceEqual("Runtime.bindingCalled"u8)
            && TryReadBinding(paramsUtf8, out string bindingName, out string? payload))
        {
            BindingCalled?.Invoke(bindingName, payload);
        }
        else if (ConsoleMessage is not null
            && method.SequenceEqual("Runtime.consoleAPICalled"u8)
            && TryReadConsoleText(paramsUtf8, out string consoleText))
        {
            ConsoleMessage(consoleText);
        }
        else if (ScreencastFrameReceived is not null
            && method.SequenceEqual("Page.screencastFrame"u8)
            && ScreencastFrameInfo.TryParse(paramsUtf8, out ScreencastFrameInfo frame))
        {
            ScreencastFrameReceived(frame);
        }


        UserSink?.OnEvent(sessionId, method, paramsUtf8);
    }

    private static bool TryReadConsoleText(ReadOnlySpan<byte> paramsUtf8, out string text)
    {
        text = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(paramsUtf8.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("args", out JsonElement args)
                || args.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement argument in args.EnumerateArray())
            {
                if (argument.ValueKind == JsonValueKind.Object
                    && argument.TryGetProperty("value", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString() ?? string.Empty;
                    return text.Length > 0;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadBinding(
        ReadOnlySpan<byte> paramsUtf8,
        out string name,
        out string? payload)
    {
        name = string.Empty;
        payload = null;

        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("name"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
                {
                    name = escaped ? Escape(raw) : Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("payload"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
                {
                    payload = escaped ? Escape(raw) : Encoding.UTF8.GetString(raw);
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return name.Length > 0;
    }

    private static string Escape(ReadOnlySpan<byte> raw)
    {
        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }

    private void HandleFrameAttached(ReadOnlySpan<byte> paramsUtf8)
    {
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return;
        }

        string? frameId = null;
        string? parentFrameId = null;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("frameId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    frameId = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("parentFrameId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    parentFrameId = Encoding.UTF8.GetString(raw);
                }
            }
            else if (!cursor.SkipValue())
            {
                return;
            }
        }

        if (!string.IsNullOrEmpty(frameId))
        {
            FrameAttached?.Invoke(frameId, parentFrameId);
        }
    }

    private void HandleFrameDetached(ReadOnlySpan<byte> paramsUtf8)
    {
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("frameId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    FrameDetached?.Invoke(Encoding.UTF8.GetString(raw));
                }

                return;
            }

            if (!cursor.SkipValue())
            {
                return;
            }
        }
    }

    private void HandleFrameNavigated(ReadOnlySpan<byte> paramsUtf8)
    {
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("frame"u8))
        {
            return;
        }

        if (!FrameTreeEntry.TryParse(ref cursor, inheritedParentFrameId: null, out FrameTreeEntry frame))
        {
            return;
        }

        FrameNavigated?.Invoke(frame);
        if (IsMainFrame(Encoding.UTF8.GetBytes(frame.FrameId)))
        {
            Signal(SignalKind.Commit);
            MainFrameNavigated?.Invoke();
        }
    }

    private void HandleSameDocumentNavigation(ReadOnlySpan<byte> paramsUtf8)
    {
        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return;
        }

        string? frameId = null;
        string? url = null;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("frameId"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    frameId = Encoding.UTF8.GetString(raw);
                }
            }
            else if (property.SequenceEqual("url"u8))
            {
                if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                {
                    url = Encoding.UTF8.GetString(raw);
                }
            }
            else if (!cursor.SkipValue())
            {
                return;
            }
        }

        if (frameId is null)
        {
            return;
        }

        if (url is not null)
        {
            FrameUrlChanged?.Invoke(frameId, url);
        }

        if (IsMainFrame(Encoding.UTF8.GetBytes(frameId)))
        {
            Signal(SignalKind.Commit);
        }
    }

    private void HandleLifecycle(ReadOnlySpan<byte> paramsUtf8)
    {
        ReadOnlySpan<byte> frameId = default;
        ReadOnlySpan<byte> name = default;

        JsonCursor cursor = new(paramsUtf8);
        if (!cursor.TryEnterObject())
        {
            return;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("frameId"u8))
            {
                cursor.TryReadString(out frameId, out _);
            }
            else if (property.SequenceEqual("name"u8))
            {
                cursor.TryReadString(out name, out _);
            }
            else if (!cursor.SkipValue())
            {
                return;
            }
        }

        if (frameId.IsEmpty || !IsMainFrame(frameId))
        {
            return;
        }

        if (name.SequenceEqual("load"u8))
        {
            // Load is signalled from Page.loadEventFired. Page.lifecycleEvent also reports a
            // "load" event; signalling both would leave one duplicate event in the pipe that can
            // satisfy the next navigation's waiter and make GotoAsync return for the old document.
            return;
        }

        if (name.SequenceEqual("DOMContentLoaded"u8))
        {
            Signal(SignalKind.DomContentLoaded);
            DomContentLoaded?.Invoke();
        }
    }

    private bool IsMainFrame(ReadOnlySpan<byte> frameId)
    {
        byte[]? mainFrame = _mainFrameUtf8;
        return mainFrame is not null && frameId.SequenceEqual(mainFrame);
    }

    private void Signal(SignalKind kind)
    {
        NavigationWaiter? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        pending?.Signal(kind);
    }
}

internal enum SignalKind
{
    Commit = 0,
    DomContentLoaded = 1,
    Load = 2,
}

/// <summary>One in-flight navigation waiting for its completion signal.</summary>
internal sealed class NavigationWaiter
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NavigationWaiter(WaitUntil waitUntil) => WaitUntil = waitUntil;

    public WaitUntil WaitUntil { get; }

    public Task Completion => _completion.Task;

    public void Signal(SignalKind kind)
    {
        if ((int)kind >= (int)WaitUntil)
        {
            _completion.TrySetResult(true);
        }
    }

    public void Fail(Exception exception) => _completion.TrySetException(exception);

    public void Abandon(string reason) => _completion.TrySetException(new CdpException(reason));
}

/// <summary>A navigation was rejected by the browser, for example <c>net::ERR_NAME_NOT_RESOLVED</c>.</summary>
public sealed class CdpNavigationException : CdpException
{
    public CdpNavigationException(string url, string errorText)
        : base($"Navigation to '{url}' failed: {errorText}")
    {
        Url = url;
        ErrorText = errorText;
    }

    public string Url { get; }

    public string ErrorText { get; }

    public static string Describe(ReadOnlySpan<byte> urlUtf8, int maxLength = 200)
    {
        int length = Math.Min(urlUtf8.Length, maxLength);
        return Encoding.UTF8.GetString(urlUtf8[..length]);
    }
}
