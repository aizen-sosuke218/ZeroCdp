using System.Text;
using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>What to do when a request has no matching HAR entry.</summary>
public enum HarNotFoundAction
{
    /// <summary>Fail the request locally. This is Playwright's default.</summary>
    Abort = 0,

    /// <summary>Pass the request to the next route or the network.</summary>
    Fallback = 1,
}

/// <summary>Options for <see cref="BrowserContext.RouteFromHARAsync"/>.</summary>
public sealed class HarReplayOptions
{
    /// <summary>
    /// Only handle URLs matching this exact URL or glob. Other requests fall through to the
    /// network. Null handles every request in the archive.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Action for requests that have no matching HAR entry. Defaults to abort.</summary>
    public HarNotFoundAction NotFound { get; init; } = HarNotFoundAction.Abort;
}

/// <summary>Read-only HAR 1.2 response replay.</summary>
internal sealed class HarPlayer
{
    private readonly IReadOnlyList<HarResponse> _entries;
    private readonly string? _urlFilter;
    private readonly HarNotFoundAction _notFound;

    private HarPlayer(
        IReadOnlyList<HarResponse> entries,
        string? urlFilter,
        HarNotFoundAction notFound)
    {
        _entries = entries;
        _urlFilter = urlFilter;
        _notFound = notFound;
    }

    public static async ValueTask<HarPlayer> LoadAsync(
        string path,
        HarReplayOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] json = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("log", out JsonElement log)
            || log.ValueKind != JsonValueKind.Object
            || !log.TryGetProperty("entries", out JsonElement entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The file is not a HAR 1.2 archive with a log.entries array.");
        }

        List<HarResponse> entries = new();
        foreach (JsonElement element in entriesElement.EnumerateArray())
        {
            HarResponse? entry = ReadEntry(element);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        options ??= new HarReplayOptions();
        return new HarPlayer(entries, options.Url, options.NotFound);
    }

    public async ValueTask HandleAsync(Route route, CancellationToken cancellationToken)
    {
        string url = route.Request.Url;
        if (_urlFilter is not null && !Matches(_urlFilter, url))
        {
            await route.FallbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        HarResponse? match = Find(route.Request.Method, url);
        if (match is null)
        {
            if (_notFound == HarNotFoundAction.Fallback)
            {
                await route.FallbackAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await route.AbortAsync("Failed", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await route
            .FulfillAsync(
                match.Status,
                match.Body,
                contentType: null,
                headers: match.Headers,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private HarResponse? Find(string method, string url)
    {
        foreach (HarResponse entry in _entries)
        {
            if (entry.Method.Equals(method, StringComparison.Ordinal)
                && entry.Url.Equals(url, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool Matches(string pattern, string url)
    {
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            return UrlGlob.IsMatch(pattern, url);
        }

        return pattern.Equals(url, StringComparison.Ordinal);
    }

    private static HarResponse? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("request", out JsonElement request)
            || request.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("response", out JsonElement response)
            || response.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string method = ReadString(request, "method");
        string url = ReadString(request, "url");
        if (method.Length == 0 || url.Length == 0)
        {
            return null;
        }

        int status = 200;
        if (response.TryGetProperty("status", out JsonElement statusElement)
            && statusElement.ValueKind == JsonValueKind.Number
            && statusElement.TryGetInt32(out int parsedStatus)
            && parsedStatus is >= 100 and <= 599)
        {
            status = parsedStatus;
        }

        string mimeType = string.Empty;
        string? text = null;
        bool base64 = false;
        if (response.TryGetProperty("content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Object)
        {
            mimeType = ReadString(content, "mimeType");
            text = ReadNullableString(content, "text");
            string encoding = ReadString(content, "encoding");
            base64 = encoding.Equals("base64", StringComparison.OrdinalIgnoreCase);
        }

        byte[] body = text is null
            ? Array.Empty<byte>()
            : base64
                ? DecodeBase64(text)
                : Encoding.UTF8.GetBytes(text);

        List<CdpHeader> headers = new();
        if (response.TryGetProperty("headers", out JsonElement headersElement)
            && headersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement headerElement in headersElement.EnumerateArray())
            {
                if (headerElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = ReadString(headerElement, "name");
                string value = ReadString(headerElement, "value");
                if (name.Length == 0 || IsReplayUnsafeHeader(name))
                {
                    continue;
                }

                headers.Add(new CdpHeader(name, value));
            }
        }

        if (!HasHeader(headers, "content-type") && mimeType.Length > 0)
        {
            headers.Add(new CdpHeader("content-type", mimeType));
        }

        return new HarResponse
        {
            Method = method,
            Url = url,
            Status = status,
            Headers = headers,
            Body = body,
        };
    }

    private static bool IsReplayUnsafeHeader(string name)
        => name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("keep-alive", StringComparison.OrdinalIgnoreCase);

    private static bool HasHeader(IReadOnlyList<CdpHeader> headers, string name)
    {
        foreach (CdpHeader header in headers)
        {
            if (header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] DecodeBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(text);
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
        => ReadNullableString(element, propertyName) ?? string.Empty;

    private static string? ReadNullableString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class HarResponse
    {
        public string Method { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public int Status { get; init; } = 200;

        public IReadOnlyList<CdpHeader> Headers { get; init; } = Array.Empty<CdpHeader>();

        public byte[] Body { get; init; } = Array.Empty<byte>();
    }
}
