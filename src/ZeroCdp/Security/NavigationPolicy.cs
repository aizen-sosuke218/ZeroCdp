namespace ZeroCdp.Security;

/// <summary>
/// Allow-list for top-level navigation.
/// </summary>
/// <remarks>
/// <para>
/// This is a guard against a page steering automation somewhere it should not go, not a network
/// firewall. It runs before <c>Page.navigate</c> is sent; a server-side redirect after that is a
/// job for request interception (planned for v2), because CDP reports the final URL only after
/// the navigation committed.
/// </para>
/// <para>
/// Origins are compared as <c>scheme://host[:port]</c>, case-insensitively. <c>about:blank</c> is
/// always allowed: it is the blank document every new target starts from.
/// </para>
/// </remarks>
public sealed class NavigationPolicy
{
    private readonly string[] _allowedOrigins;

    /// <summary>A policy that permits every absolute URL.</summary>
    public static NavigationPolicy AllowAll { get; } = new([]);

    /// <summary>Creates a policy. Passing no origins means "allow everything".</summary>
    /// <param name="allowedOrigins">
    /// Absolute origins, for example <c>https://example.com</c> or <c>file://</c>.
    /// Wildcards are rejected: a policy that allows everything is what <see cref="AllowAll"/> is for.
    /// </param>
    public NavigationPolicy(params string[] allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        _allowedOrigins = new string[allowedOrigins.Length];
        for (int i = 0; i < allowedOrigins.Length; i++)
        {
            string value = allowedOrigins[i];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("An allowed origin cannot be empty.", nameof(allowedOrigins));
            }

            value = value.Trim();
            if (value.Contains('*', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Wildcard origins are not supported: '{value}'. Use {nameof(AllowAll)} explicitly.",
                    nameof(allowedOrigins));
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) || string.IsNullOrEmpty(origin.Host) && origin.Scheme != Uri.UriSchemeFile)
            {
                throw new ArgumentException($"'{value}' is not an absolute origin.", nameof(allowedOrigins));
            }

            _allowedOrigins[i] = origin.GetLeftPart(UriPartial.Authority);
        }
    }

    /// <summary>True when <paramref name="uri"/> may be navigated to.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (_allowedOrigins.Length == 0)
        {
            return true;
        }

        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string origin = uri.GetLeftPart(UriPartial.Authority);
        foreach (string allowed in _allowedOrigins)
        {
            if (origin.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses and checks a URL, throwing <see cref="NavigationBlockedException"/> when denied.</summary>
    public void Validate(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new NavigationBlockedException(url, "The URL is not absolute.");
        }

        Validate(uri);
    }

    /// <summary>Checks a parsed URI, throwing <see cref="NavigationBlockedException"/> when denied.</summary>
    public void Validate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowed(uri))
        {
            throw new NavigationBlockedException(uri.AbsoluteUri, "The origin is not on the navigation allow-list.");
        }
    }
}

/// <summary>A navigation was refused by <see cref="NavigationPolicy"/> before any CDP command ran.</summary>
public sealed class NavigationBlockedException : CdpException
{
    public NavigationBlockedException(string url, string reason)
        : base($"Navigation to '{url}' was blocked: {reason}")
    {
        Url = url;
        Reason = reason;
    }

    public string Url { get; }

    public string Reason { get; }
}
