using System.Text;
using ZeroCdp.Dom;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Selectors;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Creates a locator for <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">CSS selector.</param>
    /// <param name="pierceShadowDom">
    /// Evaluate the selector across shadow boundaries, including closed roots. The browser resolves
    /// a plain selector itself; piercing needs a DOM snapshot and the managed matcher, so it is
    /// opt-in and slower.
    /// </param>
    public Locator Locator(string selector, bool pierceShadowDom = false, TimeSpan? timeout = null)
        => new(this, selector, pierceShadowDom, frame: null, timeout);

    /// <summary>
    /// Finds elements by normalized text content, matching the deepest element that contains it.
    /// Semantic locators use a pierced DOM snapshot, so they also reach closed shadow roots.
    /// </summary>
    public Locator GetByText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(this, new LocatorSpec(LocatorKind.Text, text, exact), timeout);
    }

    /// <summary>
    /// Finds elements by ARIA role and, optionally, accessible name. Common implicit roles for
    /// native HTML elements are supported; complex ARIA name computation is approximate.
    /// </summary>
    public Locator GetByRole(
        string role,
        string? name = null,
        bool exact = false,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return new Locator(this, new LocatorSpec(LocatorKind.Role, role, exact, name), timeout);
    }

    /// <summary>Finds a labelable control by its associated label text.</summary>
    public Locator GetByLabel(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(this, new LocatorSpec(LocatorKind.Label, text, exact), timeout);
    }

    /// <summary>Finds an input or textarea by its <c>placeholder</c> attribute.</summary>
    public Locator GetByPlaceholder(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(this, new LocatorSpec(LocatorKind.Placeholder, text, exact), timeout);
    }

    /// <summary>Finds an element by its <c>data-testid</c> attribute.</summary>
    public Locator GetByTestId(string testId, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(testId);
        return new Locator(this, new LocatorSpec(LocatorKind.TestId, testId), timeout);
    }

    /// <summary>Finds an <c>img</c>, <c>area</c> or image input by its <c>alt</c> text.</summary>
    public Locator GetByAltText(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(this, new LocatorSpec(LocatorKind.AltText, text, exact), timeout);
    }

    /// <summary>Finds an element by its <c>title</c> attribute.</summary>
    public Locator GetByTitle(string text, bool exact = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Locator(this, new LocatorSpec(LocatorKind.Title, text, exact), timeout);
    }

    /// <summary>
    /// Targets an <c>iframe</c> or <c>frame</c> element and returns locators scoped to its document.
    /// </summary>
    /// <remarks>
    /// The frame is resolved lazily on every action, so it survives a navigation or a re-render of
    /// the parent. Frame-scoped queries run in an isolated world belonging to that frame; closed
    /// shadow roots inside a frame are not pierced in this version.
    /// </remarks>
    public FrameLocator FrameLocator(string selector)
        => new(this, parent: null, selector, pierceShadowDom: false);

    /// <summary>Finds an element by ARIA role with Playwright-style filters.</summary>
    public Locator GetByRole(string role, RoleOptions options, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(options);
        return new Locator(this, new LocatorSpec(LocatorKind.Role, role, options: options), timeout);
    }

    /// <summary>Playwright-style role lookup using the typed <see cref="AriaRole"/> enum.</summary>
    public Locator GetByRole(
        AriaRole role,
        RoleOptions? options = null,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), options ?? new RoleOptions(), timeout);

    /// <summary>Playwright-style role lookup with an accessible name.</summary>
    public Locator GetByRole(
        AriaRole role,
        string name,
        bool exact = false,
        TimeSpan? timeout = null)
        => GetByRole(role.ToRoleName(), name, exact, timeout);

    /// <summary>
    /// Fetches the document root node id.
    /// </summary>
    /// <remarks>
    /// Deliberately not cached. Asking the DOM agent for the document again (which the
    /// shadow-piercing path does with different arguments) invalidates previously handed out node
    /// ids, so a cached root turns into "Could not find node with given id" after the first
    /// snapshot. One extra round trip buys correctness.
    /// </remarks>
    internal async ValueTask<long> GetDocumentNodeIdAsync(CancellationToken cancellationToken)
        => await GetDocumentNodeIdAsync(MainFrameContext, cancellationToken)
            .ConfigureAwait(false);

    internal static async ValueTask<long> GetDocumentNodeIdAsync(
        FrameContext context,
        CancellationToken cancellationToken)
    {
        DocumentRoot root = await context.Session
            .SendAsync<DocumentRoot, GetDocumentParams>(
                "DOM.getDocument"u8,
                new GetDocumentParams(depth: 1, pierce: false),
                cancellationToken)
            .ConfigureAwait(false);

        return root.NodeId;
    }

    internal async ValueTask<long[]> ResolveByQueryAsync(string selector, CancellationToken cancellationToken)
    {
        long root = await GetDocumentNodeIdAsync(cancellationToken).ConfigureAwait(false);

        NodeIdsResult result = await Session
            .SendAsync<NodeIdsResult, QuerySelectorAllParams>(
                "DOM.querySelectorAll"u8,
                new QuerySelectorAllParams(root, selector),
                cancellationToken)
            .ConfigureAwait(false);

        return result.NodeIds;
    }

    /// <summary>
    /// Matches a selector against a pierced DOM snapshot.
    /// </summary>
    /// <remarks>
    /// This is what reaches closed shadow roots: <c>DOM.getDocument</c> with <c>pierce</c> exposes
    /// them to the debugger, and the managed matcher walks the tree itself.
    /// </remarks>
    internal async ValueTask<long[]> ResolveByMatcherAsync(
        ZeroCdp.Selectors.CompiledSelector[] selectors,
        CancellationToken cancellationToken)
    {
        Dom.DomNode? root = await GetSnapshotRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null
            ? []
            : TraverseMatches(root, node => ZeroCdp.Selectors.SelectorMatcher.MatchesAny(selectors, node));
    }

    /// <summary>Resolves a semantic locator against a pierced DOM snapshot.</summary>
    internal async ValueTask<long[]> ResolveBySpecAsync(
        LocatorSpec spec,
        CancellationToken cancellationToken)
    {
        Dom.DomNode? root = await GetSnapshotRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        SemanticContext semantic = new(root);
        return TraverseMatches(root, node => LocatorSpecMatcher.Matches(spec, node, semantic));
    }

    /// <summary>Evaluates XPath against a pierced snapshot of the page document.</summary>
    internal async ValueTask<long[]> ResolveByXPathAsync(
        ZeroCdp.Selectors.XPathSelector selector,
        CancellationToken cancellationToken)
    {
        Dom.DomNode? root = await GetSnapshotRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null ? [] : selector.Resolve(root);
    }

    internal async ValueTask<long[]> ResolveInFrameByXPathAsync(
        FrameScope frame,
        ZeroCdp.Selectors.XPathSelector selector,
        CancellationToken cancellationToken)
    {
        FrameContext context = await ResolveFrameContextAsync(frame, cancellationToken).ConfigureAwait(false);
        Dom.DomNode? root = await GetFrameSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
        return root is null ? [] : EncodeNodeIds(context, selector.Resolve(root));
    }

    /// <summary>Evaluates XPath inside the subtree of each parent node.</summary>

    private async ValueTask<FrameContext> ResolveFrameContextAsync(
        FrameScope frame,
        CancellationToken cancellationToken)
    {
        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        return GetFrameContext(frameId);
    }

    private static async ValueTask<Dom.DomNode?> GetFrameSnapshotRootAsync(
        FrameContext context,
        CancellationToken cancellationToken)
    {
        if (context.IsOopif)
        {
            return await GetSnapshotRootForContextAsync(context, cancellationToken).ConfigureAwait(false);
        }

        Dom.DomNode? root = await GetSnapshotRootAsync(context, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        Dom.DomNode? owner = FindFrameOwner(root, context.FrameId);
        if (owner?.Children is null)
        {
            return null;
        }

        foreach (Dom.DomNode child in owner.Children)
        {
            if (child.IsDocument)
            {
                return child;
            }
        }

        return null;
    }

    private static Dom.DomNode? FindFrameOwner(Dom.DomNode root, string frameId)
    {
        Stack<Dom.DomNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Dom.DomNode node = pending.Pop();
            if (node.FrameId is { Length: > 0 } id && id.Equals(frameId, StringComparison.Ordinal))
            {
                return node;
            }

            foreach (Dom.DomNode child in node.TraversalChildren())
            {
                pending.Push(child);
            }
        }

        return null;
    }

    private async ValueTask<Dom.DomNode?> GetSnapshotRootAsync(CancellationToken cancellationToken)
        => await GetSnapshotRootAsync(MainFrameContext, cancellationToken)
            .ConfigureAwait(false);

    private static async ValueTask<Dom.DomNode?> GetSnapshotRootAsync(
        FrameContext context,
        CancellationToken cancellationToken)
    {
        DocumentTree tree = await context.Session
            .SendAsync<DocumentTree, GetDocumentParams>(
                "DOM.getDocument"u8,
                new GetDocumentParams(
                    depth: context.Session.Connection.Options.MaxDomDepth,
                    pierce: true),
                cancellationToken)
            .ConfigureAwait(false);

        return Dom.DomTreeParser.Parse(tree.RootJson.Span);
    }

    private static long[] TraverseMatches(Dom.DomNode root, Func<Dom.DomNode, bool> predicate)
    {
        List<long> matches = new();
        Stack<Dom.DomNode> pending = new();
        pending.Push(root);
        List<Dom.DomNode> children = new();

        while (pending.Count > 0)
        {
            Dom.DomNode node = pending.Pop();

            // A snapshot can contain iframe content documents. A selector evaluated in one
            // document must not leak across a frame boundary; the frame-specific resolvers start
            // from the requested document instead.
            if (!ReferenceEquals(node, root) && node.NodeType == Dom.DomNode.DocumentNode)
            {
                continue;
            }

            if (node.IsElement && node.NodeId != 0 && predicate(node))
            {
                matches.Add(node.NodeId);
            }

            children.Clear();
            foreach (Dom.DomNode child in node.TraversalChildren())
            {
                children.Add(child);
            }

            // Push in reverse so the stack pops children in document order.
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        return matches.ToArray();
    }
}
