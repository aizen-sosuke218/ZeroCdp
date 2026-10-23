using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    /// <summary>Text content of every match, or of the selected nth match.</summary>
    public async ValueTask<string[]> AllTextContentsAsync(CancellationToken cancellationToken = default)
    {
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        if (nodes.Length == 0)
        {
            return [];
        }

        string[] values = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            values[i] = await _page.NodeTextAsync(nodes[i], cancellationToken).ConfigureAwait(false);
        }

        return values;
    }

    /// <summary>Rendered <c>innerText</c> of every match.</summary>
    public async ValueTask<string[]> AllInnerTextsAsync(CancellationToken cancellationToken = default)
    {
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        if (nodes.Length == 0)
        {
            return [];
        }

        string[] values = new string[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            values[i] = await _page.GetInnerTextAsync(nodes[i], cancellationToken).ConfigureAwait(false);
        }

        return values;
    }

    /// <summary>Value of an attribute on the first match, or null when it is absent.</summary>
    public ValueTask<string?> GetAttributeAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                DomNode? info = await _page.GetNodeInfoAsync(nodeId, token).ConfigureAwait(false);
                return info?.GetAttribute(name);
            },
            cancellationToken);
    }

    /// <summary>Live <c>value</c> of the first input, textarea or select.</summary>
    public ValueTask<string?> InputValueAsync(CancellationToken cancellationToken = default)
        => RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                return await _page.GetInputValueAsync(nodeId, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Rendered <c>innerText</c> of the first match.</summary>
    public ValueTask<string> InnerTextAsync(CancellationToken cancellationToken = default)
        => RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                return await _page.GetInnerTextAsync(nodeId, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Captures the first match as an image.</summary>

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
        => ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false)).Length;

    /// <summary>True when the first match has a non-empty box. Does not wait.</summary>
    public async ValueTask<bool> IsVisibleAsync(CancellationToken cancellationToken = default)
    {
        long nodeId;
        try
        {
            nodeId = await FindFirstAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ElementNotFoundException)
        {
            return false;
        }

        return nodeId != 0 && await _page.HasBoxAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the first match is hidden or absent. Does not wait.</summary>
    public async ValueTask<bool> IsHiddenAsync(CancellationToken cancellationToken = default)
        => !await IsVisibleAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>True when the first match is not disabled. Does not wait.</summary>
    public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        DomNode? info = await TryGetFirstInfoAsync(cancellationToken).ConfigureAwait(false);
        return info is not null && !IsDisabled(info);
    }

    /// <summary>True when the first match is disabled. Does not wait.</summary>
    public async ValueTask<bool> IsDisabledAsync(CancellationToken cancellationToken = default)
    {
        DomNode? info = await TryGetFirstInfoAsync(cancellationToken).ConfigureAwait(false);
        return info is not null && IsDisabled(info);
    }

    /// <summary>True when the first match accepts text input. Does not wait.</summary>
    public async ValueTask<bool> IsEditableAsync(CancellationToken cancellationToken = default)
    {
        DomNode? info = await TryGetFirstInfoAsync(cancellationToken).ConfigureAwait(false);
        return info is not null && IsEditable(info);
    }

    /// <summary>True when the first match is a checked checkbox or radio. Does not wait.</summary>
    public async ValueTask<bool> IsCheckedAsync(CancellationToken cancellationToken = default)
    {
        long nodeId;
        try
        {
            nodeId = await FindFirstAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ElementNotFoundException)
        {
            return false;
        }

        return nodeId != 0 && await _page.IsCheckedAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The visible bounding box of the first match, or <see langword="null"/> when it has no box.
    /// </summary>
    public ValueTask<BoundingBox?> BoundingBoxAsync(CancellationToken cancellationToken = default)
        => RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                return await _page.GetNodeBoundingBoxAsync(nodeId, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Text content of the first match (<c>textContent</c>, not rendered text).</summary>
    public ValueTask<string> TextContentAsync(CancellationToken cancellationToken = default)
        => TextAsync(cancellationToken);

    /// <summary>Text content of the first match (<c>textContent</c>, not rendered text).</summary>
    public ValueTask<string> TextAsync(CancellationToken cancellationToken = default)
        => RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                return await _page.NodeTextAsync(nodeId, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Playwright-style <c>innerHTML</c> of the first match.</summary>
    public ValueTask<string> InnerHTMLAsync(CancellationToken cancellationToken = default)
        => RetryStaleNodeAsync(
            async (timeout, token) =>
            {
                long nodeId = await WaitForActionAsync(
                        requireVisible: false,
                        requireEnabled: false,
                        requireEditable: false,
                        requirement: "attached",
                        token,
                        timeout)
                    .ConfigureAwait(false);

                EvaluationResult result = await _page
                    .EvaluateOnNodeAsync(nodeId, "node => node.innerHTML", token)
                    .ConfigureAwait(false);
                return result.Value.StringValue ?? string.Empty;
            },
            cancellationToken);

    /// <summary>
    /// Returns a Playwright-style YAML aria snapshot of the locator's first match. The element is
    /// resolved through the normal actionability path, so the call waits for attachment.
    /// </summary>
    public ValueTask<string> AriaSnapshotAsync(CancellationToken cancellationToken = default)
        => AriaSnapshotAsync(options: null, cancellationToken);

    /// <summary>
    /// Returns a Playwright-style YAML aria snapshot of the locator's first match.
    /// </summary>
    public async ValueTask<string> AriaSnapshotAsync(
        LocatorAriaSnapshotOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (options?.Timeout is { } timeout)
        {
            ValidateTimeout(timeout);
        }

        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken,
                options?.Timeout)
            .ConfigureAwait(false);

        return await _page
            .GetAriaSnapshotAsync(nodeId, options?.InterestingOnly ?? true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsDisabled(DomNode node)
    {
        if (node.HasAttribute("disabled"))
        {
            return true;
        }

        string? ariaDisabled = node.GetAttribute("aria-disabled");
        return ariaDisabled is not null && ariaDisabled.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEditable(DomNode node)
    {
        string tag = node.LocalName;
        if (tag == "textarea")
        {
            return !node.HasAttribute("readonly");
        }

        if (tag == "input")
        {
            string? type = node.GetAttribute("type");
            if (type is not null
                && (type.Equals("button", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("radio", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("submit", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("reset", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("file", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("image", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("color", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("range", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("hidden", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return !node.HasAttribute("readonly");
        }

        string? contentEditable = node.GetAttribute("contenteditable");
        return contentEditable is not null
            && !contentEditable.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
