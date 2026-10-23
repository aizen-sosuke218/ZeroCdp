using System.Text;
using System.Text.RegularExpressions;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// Web-first assertions for a <see cref="Locator"/>. Every assertion polls until it passes or its
/// timeout expires. Use <see cref="Not"/> to invert the expected state.
/// </summary>
public sealed class LocatorAssertions
{
    private readonly Locator _locator;
    private readonly bool _negated;

    internal LocatorAssertions(Locator locator, bool negated = false)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _negated = negated;
    }

    /// <summary>Inverts the next assertion.</summary>
    public LocatorAssertions Not => new(_locator, !_negated);

    private bool Expected => !_negated;

    private ValueTask PollAsync(
        string description,
        Func<CancellationToken, ValueTask<bool>> predicate,
        ExpectOptions? options,
        CancellationToken cancellationToken)
        => AssertionHelpers.RetryAsync(
            description,
            predicate,
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToBeAttachedAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be attached",
            async token => await _locator.CountAsync(token).ConfigureAwait(false) > 0,
            options,
            cancellationToken);

    public ValueTask ToBeVisibleAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be visible",
            token => _locator.IsVisibleAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeHiddenAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be hidden",
            token => _locator.IsHiddenAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeEnabledAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be enabled",
            token => _locator.IsEnabledAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeDisabledAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be disabled",
            token => _locator.IsDisabledAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeEditableAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be editable",
            token => _locator.IsEditableAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeCheckedAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be checked",
            token => _locator.IsCheckedAsync(token),
            options,
            cancellationToken);

    public ValueTask ToBeFocusedAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be focused",
            async token =>
            {
                EvaluationResult result = await _locator
                    .EvaluateAsync("node => document.activeElement === node", token)
                    .ConfigureAwait(false);
                return !result.Threw
                    && result.Value.HasValue
                    && result.Value.TryGetBoolean(out bool focused)
                    && focused;
            },
            options,
            cancellationToken);

    public ValueTask ToBeEmptyAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be empty",
            async token =>
            {
                try
                {
                    string text = await _locator.InnerTextAsync(token).ConfigureAwait(false);
                    return text.Length == 0;
                }
                catch (ElementNotFoundException)
                {
                    return false;
                }
            },
            options,
            cancellationToken);

    public ValueTask ToBeInViewportAsync(
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => PollAsync(
            "locator to be in the viewport",
            async token =>
            {
                EvaluationResult result = await _locator
                    .EvaluateAsync(
                        "node => { const r = node.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth; }",
                        token)
                    .ConfigureAwait(false);
                return !result.Threw
                    && result.Value.HasValue
                    && result.Value.TryGetBoolean(out bool inside)
                    && inside;
            },
            options,
            cancellationToken);

    public ValueTask ToHaveCountAsync(
        int count,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator to have count {count}",
            async token => await _locator.CountAsync(token).ConfigureAwait(false) == count,
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveTextAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ToHaveTextCoreAsync(expected, options, cancellationToken);

    public ValueTask ToHaveTextAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator text to match {expected}",
            async token => AssertionHelpers.Matches(await FirstInnerTextAsync(token).ConfigureAwait(false), expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToContainTextAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator text to contain {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await FirstInnerTextAsync(token).ConfigureAwait(false),
                expected,
                exact: false),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToContainTextAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator text to contain {expected}",
            async token => AssertionHelpers.Matches(await FirstInnerTextAsync(token).ConfigureAwait(false), expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveTextAsync(
        IReadOnlyList<string> expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator texts to equal [{string.Join(", ", expected)}]",
            async token =>
            {
                string[] actual = await _locator.AllInnerTextsAsync(token).ConfigureAwait(false);
                if (actual.Length != expected.Count)
                {
                    return false;
                }

                for (int i = 0; i < actual.Length; i++)
                {
                    if (!AssertionHelpers.Matches(actual[i], expected[i], exact: true))
                    {
                        return false;
                    }
                }

                return true;
            },
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToContainTextAsync(
        IReadOnlyList<string> expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator texts to contain [{string.Join(", ", expected)}]",
            async token =>
            {
                string[] actual = await _locator.AllInnerTextsAsync(token).ConfigureAwait(false);
                if (actual.Length < expected.Count)
                {
                    return false;
                }

                for (int i = 0; i < expected.Count; i++)
                {
                    if (!AssertionHelpers.Matches(actual[i], expected[i], exact: false))
                    {
                        return false;
                    }
                }

                return true;
            },
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveValueAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator value to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await _locator.InputValueAsync(token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveValuesAsync(
        IReadOnlyList<string> expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator values to equal [{string.Join(", ", expected)}]",
            async token =>
            {
                string[] actual = await GetSelectedValuesAsync(token).ConfigureAwait(false);
                return actual.SequenceEqual(expected, StringComparer.Ordinal);
            },
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveAttributeAsync(
        string name,
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator to have attribute {AssertionHelpers.FormatValue(name)} with value {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await _locator.GetAttributeAsync(name, token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveAttributeAsync(
        string name,
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator to have attribute {AssertionHelpers.FormatValue(name)} matching {expected}",
            async token => AssertionHelpers.Matches(
                await _locator.GetAttributeAsync(name, token).ConfigureAwait(false),
                expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveClassAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ToHaveAttributeAsync("class", expected, options, cancellationToken);

    public ValueTask ToHaveClassAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => ToHaveAttributeAsync("class", expected, options, cancellationToken);

    public ValueTask ToHaveIdAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ToHaveAttributeAsync("id", expected, options, cancellationToken);

    public ValueTask ToHaveCSSAsync(
        string name,
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator CSS {AssertionHelpers.FormatValue(name)} to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await ReadCssAsync(name, token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveCSSAsync(
        string name,
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator CSS {AssertionHelpers.FormatValue(name)} to match {expected}",
            async token => AssertionHelpers.Matches(await ReadCssAsync(name, token).ConfigureAwait(false), expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveRoleAsync(
        string expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator role to equal {AssertionHelpers.FormatValue(expected)}",
            async token => string.Equals(
                await ReadRoleAsync(token).ConfigureAwait(false),
                expected,
                StringComparison.Ordinal),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveAccessibleNameAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator accessible name to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await ReadAccessibleNameAsync(token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveAccessibleNameAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator accessible name to match {expected}",
            async token => AssertionHelpers.Matches(
                await ReadAccessibleNameAsync(token).ConfigureAwait(false),
                expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveAccessibleDescriptionAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"locator accessible description to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await ReadAccessibleDescriptionAsync(token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToMatchAriaSnapshotAsync(
        string expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            "locator aria snapshot to match",
            async token => string.Equals(
                NormalizeAria(await _locator.AriaSnapshotAsync(token).ConfigureAwait(false)),
                NormalizeAria(expected),
                StringComparison.Ordinal),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveScreenshotAsync(
        string? name = null,
        LocatorScreenshotAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocatorScreenshotAssertionOptions();
        string baseline = SnapshotPath(options.Directory, name ?? "locator");
        return AssertionHelpers.RetryAsync(
            $"locator screenshot to match {baseline}",
            async token =>
            {
                byte[] actual = await _locator
                    .ScreenshotBytesAsync(
                        new LocatorScreenshotOptions
                        {
                            Type = ScreenshotType.Png,
                            Mask = options.Mask,
                            MaskColor = options.MaskColor,
                            Animations = options.Animations,
                            Caret = options.Caret,
                            Scale = options.Scale,
                            Style = options.Style,
                            Timeout = options.Timeout,
                        },
                        token)
                    .ConfigureAwait(false);
                return SnapshotComparer.Matches(baseline, actual, options.UpdateSnapshots);
            },
            options.Timeout,
            Expected,
            cancellationToken);
    }

    private async ValueTask ToHaveTextCoreAsync(
        string expected,
        LocatorAssertionOptions? options,
        CancellationToken cancellationToken)
    {
        await AssertionHelpers.RetryAsync(
                $"locator text to equal {AssertionHelpers.FormatValue(expected)}",
                async token => AssertionHelpers.Matches(
                    await FirstInnerTextAsync(token).ConfigureAwait(false),
                    expected,
                    exact: true),
                options?.Timeout,
                Expected,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<string> FirstInnerTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _locator.InnerTextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ElementNotFoundException)
        {
            return string.Empty;
        }
    }

    private async ValueTask<string[]> GetSelectedValuesAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await _locator
            .EvaluateAsync(
                "node => Array.from(node.selectedOptions || []).map(option => option.value)",
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw || !result.Value.HasValue)
        {
            return [];
        }

        return ReadStringArray(result.Value.ValueJson.Span);
    }

    private async ValueTask<string?> ReadCssAsync(string name, CancellationToken cancellationToken)
    {
        EvaluationResult result = await _locator
            .EvaluateAsync(
                "node => getComputedStyle(node).getPropertyValue(" + JsText.Quote(name) + ")",
                cancellationToken)
            .ConfigureAwait(false);
        return result.Threw ? null : result.Value.StringValue;
    }

    private async ValueTask<string?> ReadRoleAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await _locator
            .EvaluateAsync(
                """
                node => {
                  if (node.getAttribute('role')) return node.getAttribute('role');
                  const tag = node.tagName.toLowerCase();
                  if (tag === 'a' && node.hasAttribute('href')) return 'link';
                  if (tag === 'button') return 'button';
                  if (tag === 'input') {
                    const type = (node.getAttribute('type') || 'text').toLowerCase();
                    if (type === 'checkbox') return 'checkbox';
                    if (type === 'radio') return 'radio';
                    if (type === 'submit' || type === 'button') return 'button';
                    return 'textbox';
                  }
                  if (tag === 'textarea') return 'textbox';
                  if (tag === 'select') return 'combobox';
                  if (tag === 'img') return 'img';
                  if (/^h[1-6]$/.test(tag)) return 'heading';
                  return null;
                }
                """,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Threw ? null : result.Value.StringValue;
    }

    private async ValueTask<string?> ReadAccessibleNameAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await _locator
            .EvaluateAsync(
                """
                node => {
                  const labelledBy = node.getAttribute('aria-labelledby');
                  if (labelledBy) {
                    const target = document.getElementById(labelledBy);
                    if (target) return (target.innerText || target.textContent || '').trim();
                  }
                  const aria = node.getAttribute('aria-label');
                  if (aria) return aria.trim();
                  if (node.alt) return node.alt.trim();
                  if (node.title) return node.title.trim();
                  return (node.innerText || node.textContent || '').trim();
                }
                """,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Threw ? null : result.Value.StringValue;
    }

    private async ValueTask<string?> ReadAccessibleDescriptionAsync(CancellationToken cancellationToken)
    {
        EvaluationResult result = await _locator
            .EvaluateAsync(
                "node => node.getAttribute('aria-describedby') ? ((document.getElementById(node.getAttribute('aria-describedby'))?.innerText) || '') : (node.title || '')",
                cancellationToken)
            .ConfigureAwait(false);
        return result.Threw ? null : result.Value.StringValue;
    }

    private static string[] ReadStringArray(ReadOnlySpan<byte> json)
    {
        JsonCursor cursor = new(json);
        if (!cursor.TryEnterArray())
        {
            return [];
        }

        List<string> values = new();
        while (cursor.TryNextArrayElement())
        {
            values.Add(
                cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped)
                    ? escaped
                        ? DecodeEscaped(raw)
                        : Encoding.UTF8.GetString(raw)
                    : string.Empty);
        }

        return values.ToArray();
    }

    private static string DecodeEscaped(ReadOnlySpan<byte> raw)
    {
        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }

    private static string NormalizeAria(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string SnapshotPath(string directory, string name)
    {
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        string platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        return Path.Combine(directory, safe + "-" + platform + ".png");
    }
}

/// <summary>Compares screenshot baselines without introducing an image-codec dependency.</summary>
internal static class SnapshotComparer
{
    internal static bool Matches(string path, byte[] actual, bool update)
    {
        if (update || !File.Exists(path))
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, actual);
            return true;
        }

        byte[] expected = File.ReadAllBytes(path);
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return true;
        }

        throw new CdpAssertionException(
            $"Screenshot did not match baseline '{path}' ({expected.Length} expected bytes, {actual.Length} actual bytes). " +
            "Use UpdateSnapshots to replace the baseline.");
    }
}
