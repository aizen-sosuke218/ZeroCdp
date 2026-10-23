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
    /// Serialized markup of the document element.
    /// </summary>
    /// <remarks>
    /// This is the DOM's current state, not the original response body, and it starts at
    /// <c>&lt;html&gt;</c> — the doctype is not included.
    /// </remarks>
    public async ValueTask<string> ContentAsync(CancellationToken cancellationToken = default)
    {
        DocumentRoot root = await Session
            .SendAsync<DocumentRoot, GetDocumentParams>("DOM.getDocument"u8, new GetDocumentParams(depth: 1, pierce: false), cancellationToken)
            .ConfigureAwait(false);

        OuterHtml html = await Session
            .SendAsync<OuterHtml, GetOuterHtmlParams>("DOM.getOuterHTML"u8, new GetOuterHtmlParams(root.NodeId), cancellationToken)
            .ConfigureAwait(false);

        return html.Value;
    }

    /// <summary>
    /// Cookies visible to this page, or to the given URLs. Without URLs the browser returns the
    /// whole jar for the profile.
    /// </summary>
    public ValueTask<CookiesResult> GetCookiesAsync(
        IReadOnlyList<string>? urls = null,
        CancellationToken cancellationToken = default)
        => Session.SendAsync<CookiesResult, GetCookiesParams>(
            "Network.getCookies"u8,
            new GetCookiesParams(urls),
            cancellationToken);

    /// <summary>Stores a cookie in the browser's jar for <paramref name="url"/>.</summary>
    public async ValueTask<bool> SetCookieAsync(
        string name,
        string value,
        string url,
        CancellationToken cancellationToken = default)
    {
        SetCookieResult result = await Session
            .SendAsync<SetCookieResult, SetCookieParams>(
                "Network.setCookie"u8,
                new SetCookieParams(name, value, url),
                cancellationToken)
            .ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>Accepts the dialog currently blocking the page.</summary>
    /// <param name="promptText">Text to return for a prompt; ignored for other dialog types.</param>
    /// <returns>False when no dialog was open.</returns>
}
