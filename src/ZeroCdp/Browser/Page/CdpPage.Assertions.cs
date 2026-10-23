namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>Creates web-first assertions for a locator owned by this page.</summary>
    public LocatorAssertions Expect(Locator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return new LocatorAssertions(locator);
    }
}
