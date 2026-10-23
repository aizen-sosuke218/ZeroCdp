namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>Resolves a relative navigation against the context's BaseUrl option.</summary>
    internal string ResolveNavigationUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        string? baseUrl = Context.Options?.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            return url;
        }

        string normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return Uri.TryCreate(normalizedBase, UriKind.Absolute, out Uri? baseUri)
            && Uri.TryCreate(baseUri, url, out Uri? resolved)
            ? resolved.ToString()
            : url;
    }
}
