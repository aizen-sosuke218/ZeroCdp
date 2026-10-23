namespace ZeroCdp.Browser;

/// <summary>Playwright-compatible aliases that cannot live on <see cref="CdpPage"/> itself.</summary>
public static class PagePlaywrightExtensions
{
    /// <summary>
    /// Playwright spelling of <see cref="CdpPage.WaitForUrlAsync"/>. It is an extension because a
    /// member named <c>WaitForURLAsync</c> next to <c>WaitForUrlAsync</c> violates CA1708.
    /// </summary>
    public static ValueTask<string> WaitForURLAsync(
        this CdpPage page,
        string url,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.WaitForUrlAsync(url, timeout, cancellationToken);
    }
}
