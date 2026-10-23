namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    /// <summary>Creates web-first assertions for this locator.</summary>
    public LocatorAssertions Expect() => new(this);

    /// <summary>
    /// Resolves the first match to an <see cref="ElementHandle"/>. Waits for attachment using the
    /// locator's normal timeout.
    /// </summary>
    public async ValueTask<ElementHandle> ElementHandleAsync(
        CancellationToken cancellationToken = default)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: false,
                requireEditable: false,
                requirement: "attached",
                cancellationToken)
            .ConfigureAwait(false);
        return _page.CreateElementHandle(nodeId);
    }

    /// <summary>Resolves every current match to an <see cref="ElementHandle"/>.</summary>
    public async ValueTask<IReadOnlyList<ElementHandle>> ElementHandlesAsync(
        CancellationToken cancellationToken = default)
    {
        long[] nodeIds = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        ElementHandle[] handles = new ElementHandle[nodeIds.Length];
        for (int i = 0; i < nodeIds.Length; i++)
        {
            handles[i] = _page.CreateElementHandle(nodeIds[i]);
        }

        return handles;
    }
}
