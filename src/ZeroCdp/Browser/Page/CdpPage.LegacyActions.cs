namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>Legacy Playwright page-level click.</summary>
    public ValueTask ClickAsync(
        string selector,
        ClickOptions? options = null,
        CancellationToken cancellationToken = default)
        => Locator(selector).ClickAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level double-click.</summary>
    public ValueTask DblClickAsync(
        string selector,
        ClickOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).DblClickAsync(cancellationToken)
            : Locator(selector).DblClickAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level hover.</summary>
    public ValueTask HoverAsync(
        string selector,
        LocatorHoverOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).HoverAsync(cancellationToken)
            : Locator(selector).HoverAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level check.</summary>
    public ValueTask CheckAsync(
        string selector,
        LocatorCheckOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).CheckAsync(cancellationToken)
            : Locator(selector).CheckAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level uncheck.</summary>
    public ValueTask UncheckAsync(
        string selector,
        LocatorCheckOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).UncheckAsync(cancellationToken)
            : Locator(selector).UncheckAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level tap.</summary>
    public ValueTask TapAsync(
        string selector,
        LocatorTapOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).TapAsync(cancellationToken)
            : Locator(selector).TapAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level fill.</summary>
    public ValueTask FillAsync(
        string selector,
        string value,
        CancellationToken cancellationToken = default)
        => Locator(selector).FillAsync(value, cancellationToken);

    /// <summary>Legacy Playwright page-level type.</summary>
    public ValueTask TypeAsync(
        string selector,
        string text,
        LocatorTypeOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).TypeAsync(text, cancellationToken: cancellationToken)
            : Locator(selector).TypeAsync(text, options, cancellationToken);

    /// <summary>Legacy Playwright page-level press.</summary>
    public ValueTask PressAsync(
        string selector,
        string key,
        LocatorPressOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).PressAsync(key, cancellationToken)
            : Locator(selector).PressAsync(key, options, cancellationToken);

    /// <summary>Legacy Playwright page-level select-option.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        string selector,
        IReadOnlyList<string> values,
        LocatorSelectOptionOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).SelectOptionAsync(values, cancellationToken)
            : Locator(selector).SelectOptionAsync(values, options, cancellationToken);

    /// <summary>Legacy Playwright page-level select-option by value, label or index.</summary>
    public ValueTask<string[]> SelectOptionAsync(
        string selector,
        LocatorSelectOptionOptions options,
        CancellationToken cancellationToken = default)
        => Locator(selector).SelectOptionAsync(options, cancellationToken);

    /// <summary>Legacy Playwright page-level set-input-files.</summary>
    public ValueTask SetInputFilesAsync(
        string selector,
        IReadOnlyList<string> files,
        LocatorSetInputFilesOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).SetInputFilesAsync(files, cancellationToken)
            : Locator(selector).SetInputFilesAsync(files, options, cancellationToken);

    /// <summary>Legacy Playwright page-level drag-and-drop.</summary>
    public async ValueTask DragAndDropAsync(
        string source,
        string target,
        CancellationToken cancellationToken = default)
        => await Locator(source).DragToAsync(Locator(target), cancellationToken).ConfigureAwait(false);

    /// <summary>Playwright-style page-level drag-and-drop with action options.</summary>
    public async ValueTask DragAndDropAsync(
        string source,
        string target,
        LocatorDragToOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await Locator(source)
            .DragToAsync(Locator(target), options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Legacy Playwright page-level dispatch-event.</summary>
    public ValueTask<bool> DispatchEventAsync(
        string selector,
        string type,
        bool bubbles = true,
        bool cancelable = true,
        CancellationToken cancellationToken = default)
        => Locator(selector).DispatchEventAsync(type, bubbles, cancelable, cancellationToken);

    /// <summary>Legacy Playwright page-level set-checked.</summary>
    public ValueTask SetCheckedAsync(
        string selector,
        bool checkedState,
        LocatorCheckOptions? options = null,
        CancellationToken cancellationToken = default)
        => options is null
            ? Locator(selector).SetCheckedAsync(checkedState, cancellationToken)
            : Locator(selector).SetCheckedAsync(checkedState, options, cancellationToken);

    /// <summary>Legacy Playwright page-level focus.</summary>
    public ValueTask FocusAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).FocusAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level attribute read.</summary>
    public ValueTask<string?> GetAttributeAsync(
        string selector,
        string name,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).GetAttributeAsync(name, cancellationToken);

    /// <summary>Legacy Playwright page-level <c>innerHTML</c> read.</summary>
    public ValueTask<string> InnerHTMLAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).InnerHTMLAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level <c>innerText</c> read.</summary>
    public ValueTask<string> InnerTextAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).InnerTextAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level <c>value</c> read.</summary>
    public ValueTask<string?> InputValueAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).InputValueAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level <c>textContent</c> read.</summary>
    public ValueTask<string> TextContentAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).TextContentAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level checked-state probe.</summary>
    public ValueTask<bool> IsCheckedAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsCheckedAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level disabled-state probe.</summary>
    public ValueTask<bool> IsDisabledAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsDisabledAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level editable-state probe.</summary>
    public ValueTask<bool> IsEditableAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsEditableAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level enabled-state probe.</summary>
    public ValueTask<bool> IsEnabledAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsEnabledAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level hidden-state probe.</summary>
    public ValueTask<bool> IsHiddenAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsHiddenAsync(cancellationToken);

    /// <summary>Legacy Playwright page-level visible-state probe.</summary>
    public ValueTask<bool> IsVisibleAsync(
        string selector,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => Locator(selector, timeout: timeout).IsVisibleAsync(cancellationToken);
}
