using System.Text.RegularExpressions;

namespace ZeroCdp.Browser;

/// <summary>Web-first assertions for a <see cref="CdpPage"/>.</summary>
public sealed class PageAssertions
{
    private readonly CdpPage _page;
    private readonly bool _negated;

    internal PageAssertions(CdpPage page, bool negated = false)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _negated = negated;
    }

    /// <summary>Inverts the next assertion.</summary>
    public PageAssertions Not => new(_page, !_negated);

    private bool Expected => !_negated;

    public ValueTask ToHaveTitleAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"page title to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await _page.TitleAsync(token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveTitleAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"page title to match {expected}",
            async token => AssertionHelpers.Matches(
                await _page.TitleAsync(token).ConfigureAwait(false),
                expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveURLAsync(
        string expected,
        LocatorAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"page URL to equal {AssertionHelpers.FormatValue(expected)}",
            async token => AssertionHelpers.Matches(
                await _page.UrlAsync(token).ConfigureAwait(false),
                expected,
                options?.Exact ?? true),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveURLAsync(
        Regex expected,
        ExpectOptions? options = null,
        CancellationToken cancellationToken = default)
        => AssertionHelpers.RetryAsync(
            $"page URL to match {expected}",
            async token => AssertionHelpers.Matches(
                await _page.UrlAsync(token).ConfigureAwait(false),
                expected),
            options?.Timeout,
            Expected,
            cancellationToken);

    public ValueTask ToHaveScreenshotAsync(
        string? name = null,
        LocatorScreenshotAssertionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocatorScreenshotAssertionOptions();
        string platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        string safeName = string.Concat(
            (name ?? "page").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        string baseline = Path.Combine(options.Directory, safeName + "-" + platform + ".png");

        return AssertionHelpers.RetryAsync(
            $"page screenshot to match {baseline}",
            async token =>
            {
                byte[] actual = await _page
                    .ScreenshotBytesAsync(
                        new PageScreenshotOptions
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
}

/// <summary>Entry points for web-first assertions.</summary>
public static class Assertions
{
    /// <summary>Creates assertions for a locator.</summary>
    public static LocatorAssertions Expect(Locator locator) => new(locator);

    /// <summary>Creates assertions for a page.</summary>
    public static PageAssertions Expect(CdpPage page) => new(page);
}
