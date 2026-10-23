namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Returns the first element matching a selector without waiting, or null when nothing
    /// matches. The returned handle pins the element until disposed or detached.
    /// </summary>
    public async ValueTask<ElementHandle?> QuerySelectorAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        long[] nodes = await Locator(selector).ResolveAsync(cancellationToken).ConfigureAwait(false);
        return nodes.Length == 0 ? null : CreateElementHandle(nodes[0]);
    }

    /// <summary>Returns handles for every element matching a selector without waiting.</summary>
    public async ValueTask<IReadOnlyList<ElementHandle>> QuerySelectorAllAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        long[] nodes = await Locator(selector).ResolveAsync(cancellationToken).ConfigureAwait(false);
        ElementHandle[] handles = new ElementHandle[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            handles[i] = CreateElementHandle(nodes[i]);
        }

        return handles;
    }
}
