using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    /// <summary>
    /// Keeps the matches whose own text contains <see cref="LocatorFilterOptions.HasText"/>, whose
    /// descendants match <see cref="LocatorFilterOptions.Has"/>, and so on.
    /// </summary>
    public Locator Filter(LocatorFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Locator(_page, _plan, Timeout, _index, _frame, _parent, filterSource: this, filter: options);
    }

    /// <summary>Keeps the elements matched by both this locator and <paramref name="other"/>.</summary>
    public Locator And(Locator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Locator(_page, _plan, Timeout, _index, _frame, _parent, andSource: this, andWith: other);
    }

    /// <summary>
    /// Matches the union of this locator and <paramref name="other"/>. The result is de-duplicated
    /// by backend node id so an element matched by both sides appears once.
    /// </summary>
    public Locator Or(Locator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!ReferenceEquals(_page, other._page))
        {
            throw new ArgumentException("Both locators must belong to the same page.", nameof(other));
        }

        return new Locator(_page, _plan, Timeout, _index, _frame, _parent, orSource: this, orWith: other);
    }

    /// <summary>Materialises every match as its own locator, in document order.</summary>
    public async IAsyncEnumerable<Locator> AllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long[] nodes = ApplyIndex(await ResolveAsync(cancellationToken).ConfigureAwait(false));
        for (int i = 0; i < nodes.Length; i++)
        {
            yield return new Locator(_page, _plan, Timeout, i, _frame, _parent);
        }
    }

    /// <summary>
    /// Intersects two node-id sets by <c>backendNodeId</c>.
    /// </summary>
    /// <remarks>
    /// Frontend node ids are only valid until the next <c>DOM.getDocument</c>, and resolving the
    /// second locator performs one. Backend ids survive that call, so the intersection is computed
    /// on them and the result uses the fresher (right-hand) ids that are safe to act on.
    /// </remarks>
}
