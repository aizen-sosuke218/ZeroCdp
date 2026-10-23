using System.Diagnostics;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// A committed frame in a page. Frames are stable objects: a navigation updates
/// <see cref="Url"/> and <see cref="Name"/> in place, while a detached frame keeps
/// <see cref="IsDetached"/> true if the caller still holds a reference.
/// </summary>
public sealed class Frame
{
    private readonly CdpPage _page;
    private readonly string _frameId;
    private Frame? _parent;
    private string _name;
    private string _url;
    private int _detached;

    internal Frame(
        CdpPage page,
        string frameId,
        Frame? parent,
        string name,
        string url,
        bool detached = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(frameId);
        _page = page;
        _frameId = frameId;
        _parent = parent;
        _name = name;
        _url = url;
        _detached = detached ? 1 : 0;
    }

    /// <summary>The page that owns this frame.</summary>
    public CdpPage Page => _page;

    /// <summary>Stable Chromium frame id.</summary>
    public string FrameId => _frameId;

    /// <summary>Owning frame, or null for the main frame.</summary>
    public Frame? ParentFrame => Volatile.Read(ref _parent);

    /// <summary>Live child frames in frame-tree order.</summary>
    public IReadOnlyList<Frame> ChildFrames => _page.GetChildFrames(this);

    /// <summary>Frame name from the document's <c>name</c> attribute or browsing context name.</summary>
    public string Name => Volatile.Read(ref _name);

    /// <summary>Last committed URL reported for this frame.</summary>
    public string Url => Volatile.Read(ref _url);

    /// <summary>True after Chromium has detached this frame from its parent.</summary>
    public bool IsDetached => Volatile.Read(ref _detached) != 0;

    /// <summary>Creates a locator scoped to this frame's document.</summary>
    public Locator Locator(string selector, bool pierceShadowDom = false, TimeSpan? timeout = null)
        => new(_page, selector, pierceShadowDom, this, timeout);

    public Locator GetByText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Text, text, exact), timeout, frame: this);
    }

    public Locator GetByRole(
        string role,
        string? name = null,
        bool exact = false,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return new Locator(_page, new LocatorSpec(LocatorKind.Role, role, exact, name), timeout, frame: this);
    }

    public Locator GetByRole(string role, RoleOptions options, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(options);
        return new Locator(_page, new LocatorSpec(LocatorKind.Role, role, options: options), timeout, frame: this);
    }

    public Locator GetByRole(
        AriaRole role,
        RoleOptions? options = null,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), options ?? new RoleOptions(), timeout);

    public Locator GetByRole(
        AriaRole role,
        string name,
        bool exact = false,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), name, exact, timeout);

    public Locator GetByLabel(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Label, text, exact), timeout, frame: this);
    }

    public Locator GetByPlaceholder(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Placeholder, text, exact), timeout, frame: this);
    }

    public Locator GetByTestId(string testId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(testId);
        return new Locator(_page, new LocatorSpec(LocatorKind.TestId, testId), timeout, frame: this);
    }

    public Locator GetByAltText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.AltText, text, exact), timeout, frame: this);
    }

    public Locator GetByTitle(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(_page, new LocatorSpec(LocatorKind.Title, text, exact), timeout, frame: this);
    }

    /// <summary>Targets a nested frame by its iframe/frame owner selector.</summary>
    public FrameLocator FrameLocator(string selector)
        => new(_page, this, selector, pierceShadowDom: false);

    /// <summary>Returns the first element matching a selector, or null.</summary>
    public async ValueTask<ElementHandle?> QuerySelectorAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        long[] nodes = await Locator(selector).ResolveAsync(cancellationToken).ConfigureAwait(false);
        return nodes.Length == 0 ? null : _page.CreateElementHandle(nodes[0]);
    }

    /// <summary>Returns handles for every element matching a selector.</summary>
    public async ValueTask<IReadOnlyList<ElementHandle>> QuerySelectorAllAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        long[] nodes = await Locator(selector).ResolveAsync(cancellationToken).ConfigureAwait(false);
        ElementHandle[] handles = new ElementHandle[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            handles[i] = _page.CreateElementHandle(nodes[i]);
        }

        return handles;
    }

    /// <summary>Adds a script tag to this frame's document.</summary>
    public async ValueTask AddScriptTagAsync(
        PageAddScriptTagOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string expression = await ContentTagScripts.BuildScriptAsync(options, cancellationToken)
            .ConfigureAwait(false);
        EvaluationResult result = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Adding the script tag failed.");
        }
    }

    /// <summary>Adds an inline script tag to this frame's document.</summary>
    public ValueTask AddScriptTagAsync(
        string content,
        CancellationToken cancellationToken = default)
        => AddScriptTagAsync(new PageAddScriptTagOptions { Content = content }, cancellationToken);

    /// <summary>Adds a style tag or stylesheet link to this frame's document.</summary>
    public async ValueTask AddStyleTagAsync(
        PageAddStyleTagOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string expression = await ContentTagScripts.BuildStyleAsync(options, cancellationToken)
            .ConfigureAwait(false);
        EvaluationResult result = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Adding the style tag failed.");
        }
    }

    /// <summary>Adds an inline style tag to this frame's document.</summary>
    public ValueTask AddStyleTagAsync(
        string content,
        CancellationToken cancellationToken = default)
        => AddStyleTagAsync(new PageAddStyleTagOptions { Content = content }, cancellationToken);

    /// <summary>Legacy Playwright frame-level click.</summary>
    public ValueTask ClickAsync(
        string selector,
        ClickOptions? options = null,
        CancellationToken cancellationToken = default)
        => Locator(selector).ClickAsync(options, cancellationToken);

    /// <summary>Legacy Playwright frame-level double-click.</summary>
    public ValueTask DblClickAsync(
        string selector,
        ClickOptions? options = null,
        CancellationToken cancellationToken = default)
        => Locator(selector).DblClickAsync(cancellationToken);

    /// <summary>Legacy Playwright frame-level fill.</summary>
    public ValueTask FillAsync(
        string selector,
        string value,
        CancellationToken cancellationToken = default)
        => Locator(selector).FillAsync(value, cancellationToken);

    /// <summary>Legacy Playwright frame-level type.</summary>
    public ValueTask TypeAsync(
        string selector,
        string text,
        LocatorTypeOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).TypeAsync(text, cancellationToken: cancellationToken)
            : Locator(selector).TypeAsync(text, options, cancellationToken);

    /// <summary>Legacy Playwright frame-level press.</summary>
    public ValueTask PressAsync(
        string selector,
        string key,
        LocatorPressOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).PressAsync(key, cancellationToken)
            : Locator(selector).PressAsync(key, options, cancellationToken);

    /// <summary>Legacy Playwright frame-level hover.</summary>
    public ValueTask HoverAsync(
        string selector,
        LocatorHoverOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).HoverAsync(cancellationToken)
            : Locator(selector).HoverAsync(options, cancellationToken);

    /// <summary>Legacy Playwright frame-level check.</summary>
    public ValueTask CheckAsync(
        string selector,
        CancellationToken cancellationToken = default)
        => Locator(selector).CheckAsync(cancellationToken);

    /// <summary>Legacy Playwright frame-level uncheck.</summary>
    public ValueTask UncheckAsync(
        string selector,
        CancellationToken cancellationToken = default)
        => Locator(selector).UncheckAsync(cancellationToken);

    /// <summary>Legacy Playwright frame-level set-checked.</summary>
    public ValueTask SetCheckedAsync(
        string selector,
        bool checkedState,
        CancellationToken cancellationToken = default)
        => Locator(selector).SetCheckedAsync(checkedState, cancellationToken);

    /// <summary>Legacy Playwright frame-level tap.</summary>
    public ValueTask TapAsync(
        string selector,
        CancellationToken cancellationToken = default)
        => Locator(selector).TapAsync(cancellationToken);

    /// <summary>Legacy Playwright frame-level select-option.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        string selector,
        IReadOnlyList<string> values,
        LocatorSelectOptionOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).SelectOptionAsync(values, cancellationToken)
            : Locator(selector).SelectOptionAsync(values, options, cancellationToken);

    /// <summary>Legacy Playwright frame-level select-option by value, label or index.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        string selector,
        LocatorSelectOptionOptions options,
        CancellationToken cancellationToken = default)
        => Locator(selector).SelectOptionAsync(options, cancellationToken);

    /// <summary>Legacy Playwright frame-level set-input-files.</summary>
    public ValueTask SetInputFilesAsync(
        string selector,
        IReadOnlyList<string> files,
        LocatorSetInputFilesOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).SetInputFilesAsync(files, cancellationToken)
            : Locator(selector).SetInputFilesAsync(files, options, cancellationToken);

    /// <summary>Legacy Playwright frame-level drag-and-drop.</summary>
    public async ValueTask DragAndDropAsync(
        string source,
        string target,
        CancellationToken cancellationToken = default)
        => await Locator(source).DragToAsync(Locator(target), cancellationToken).ConfigureAwait(false);

    /// <summary>Legacy Playwright frame-level dispatch-event.</summary>
    public ValueTask<bool> DispatchEventAsync(
        string selector,
        string type,
        bool bubbles = true,
        bool cancelable = true,
        CancellationToken cancellationToken = default)
        => Locator(selector).DispatchEventAsync(type, bubbles, cancelable, cancellationToken);

    /// <summary>Evaluates JavaScript in this frame's isolated world.</summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string frameId = await ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        return await _page.GetFrameRuntime(frameId)
            .EvaluateAsync(expression, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Evaluates an expression and returns a handle to the resulting JavaScript object.</summary>
    public async ValueTask<JSHandle> EvaluateHandleAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string frameId = await ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        FrameContext context = _page.GetFrameContext(frameId);
        EvaluationResult result = await context.World
            .EvaluateObjectAsync(expression, cancellationToken)
            .ConfigureAwait(false);
        return _page.CreateHandle(result, context.Id);
    }

    /// <summary>Serialized document markup for this frame.</summary>
    public async ValueTask<string> ContentAsync(CancellationToken cancellationToken = default)
    {
        EvaluationResult result = await EvaluateAsync(
                "document.documentElement ? document.documentElement.outerHTML : ''",
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Reading frame content failed.");
        }

        return result.Value.StringValue ?? string.Empty;
    }

    /// <summary>Title of this frame's document.</summary>
    public async ValueTask<string> TitleAsync(CancellationToken cancellationToken = default)
    {
        EvaluationResult result = await EvaluateAsync("document.title", cancellationToken)
            .ConfigureAwait(false);

        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Reading frame title failed.");
        }

        return result.Value.StringValue ?? string.Empty;
    }

    /// <summary>Waits until this frame's committed URL matches a literal or glob pattern.</summary>
    public async ValueTask<string> WaitForURLAsync(
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        TimeSpan effective = timeout ?? _page.DefaultTimeout;
        long deadline = Environment.TickCount64 + (long)effective.TotalMilliseconds;
        bool glob = url.Contains('*', StringComparison.Ordinal) || url.Contains('?', StringComparison.Ordinal);

        while (true)
        {
            string current = Url;
            if (glob ? UrlGlob.IsMatch(url, current) : current.Equals(url, StringComparison.Ordinal))
            {
                return current;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Frame URL did not become '{url}' within {effective.TotalSeconds:0.##}s " +
                    $"(current: '{current}').");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the frame document reaches a lifecycle state.</summary>
    public async ValueTask WaitForLoadStateAsync(
        WaitUntil state = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = timeout ?? _page.DefaultTimeout;
        long deadline = Environment.TickCount64 + (long)effective.TotalMilliseconds;
        string required = state switch
        {
            WaitUntil.Commit => "loading",
            WaitUntil.DomContentLoaded => "interactive",
            _ => "complete",
        };

        while (true)
        {
            EvaluationResult result = await EvaluateAsync("document.readyState", cancellationToken)
                .ConfigureAwait(false);

            string? readyState = result.Value.StringValue;
            if (readyState is not null
                && (readyState.Equals(required, StringComparison.Ordinal)
                    || (required == "loading" && readyState is "loading" or "interactive" or "complete")))
            {
                return;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Frame '{FrameId}' did not reach {state} within {effective.TotalSeconds:0.##}s.");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for a selector in this frame and returns its locator.</summary>
    public async ValueTask<Locator> WaitForSelectorAsync(
        string selector,
        WaitForState state = WaitForState.Visible,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Locator locator = Locator(selector, timeout: timeout);
        await locator.WaitForAsync(state, timeout, cancellationToken).ConfigureAwait(false);
        return locator;
    }

    /// <summary>Polls an expression in this frame until it becomes truthy.</summary>
    public async ValueTask<EvaluationResult> WaitForFunctionAsync(
        string expression,
        TimeSpan? timeout = null,
        TimeSpan? pollingInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        TimeSpan effective = timeout ?? _page.DefaultTimeout;
        TimeSpan polling = pollingInterval ?? TimeSpan.FromMilliseconds(50);
        if (polling <= TimeSpan.Zero)
        {
            polling = TimeSpan.FromMilliseconds(50);
        }

        long deadline = Environment.TickCount64 + (long)effective.TotalMilliseconds;

        while (true)
        {
            CdpException? expressionError = null;
            try
            {
                EvaluationResult result = await EvaluateAsync(expression, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Threw)
                {
                    expressionError = new CdpException(
                        result.ExceptionSummary ?? "The wait expression threw in the frame.");
                }
                else if (CdpPage.IsTruthy(result.Value))
                {
                    return result;
                }
            }
            catch (CdpException)
            {
                // The frame may have navigated while the expression was running.
            }

            if (expressionError is not null)
            {
                throw expressionError;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Expression '{expression}' did not become truthy in frame '{FrameId}' " +
                    $"within {effective.TotalSeconds:0.##}s.");
            }

            await Task.Delay(polling, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Delays for a fixed interval.</summary>
    public ValueTask WaitForTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _page.WaitForTimeoutAsync(timeout, cancellationToken);

    /// <summary>Navigates this frame to an absolute URL.</summary>
    public ValueTask<GotoResult> GotoAsync(
        string url,
        WaitUntil waitUntil = WaitUntil.Load,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => _page.GotoFrameAsync(this, url, waitUntil, timeout, cancellationToken);

    /// <summary>Replaces this frame's document markup.</summary>
    public ValueTask SetContentAsync(string html, CancellationToken cancellationToken = default)
        => _page.SetFrameContentAsync(this, html, cancellationToken);

    internal ValueTask<string> ResolveFrameIdAsync(CancellationToken cancellationToken)
    {
        if (IsDetached)
        {
            throw new CdpException($"Frame '{FrameId}' is detached.");
        }

        return ValueTask.FromResult(FrameId);
    }

    internal void SetParent(Frame? parent) => Volatile.Write(ref _parent, parent);

    internal void SetName(string name) => Volatile.Write(ref _name, name);

    internal void SetUrl(string url) => Volatile.Write(ref _url, url);

    internal void MarkDetached() => Volatile.Write(ref _detached, 1);
}
