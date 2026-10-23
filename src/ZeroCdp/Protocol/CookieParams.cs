using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>A cookie as the browser reports it.</summary>
public sealed class CdpCookie
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public string Domain { get; init; } = string.Empty;

    public string Path { get; init; } = "/";

    /// <summary>Optional URL used by <c>Storage.setCookies</c> instead of domain/path.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Unix seconds; -1 for a session cookie.</summary>
    public double Expires { get; init; } = -1;

    public bool HttpOnly { get; init; }

    public bool Secure { get; init; }

    public string SameSite { get; init; } = string.Empty;
}

/// <summary>Parameters of <c>Network.getCookies</c>.</summary>
public readonly struct GetCookiesParams : ICdpCommandParams
{
    private readonly IReadOnlyList<string>? _urls;

    public GetCookiesParams(IReadOnlyList<string>? urls) => _urls = urls;

    public void Write(ref CommandWriter writer)
    {
        if (_urls is null)
        {
            return;
        }

        writer.Name("urls"u8);
        writer.StringArray(_urls);
    }
}

/// <summary>Result of <c>Network.getCookies</c>.</summary>
public readonly struct CookiesResult : ICdpResult<CookiesResult>
{
    public IReadOnlyList<CdpCookie> Cookies { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out CookiesResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject() || !cursor.TrySeekProperty("cookies"u8) || !cursor.TryEnterArray())
        {
            return false;
        }

        List<CdpCookie> cookies = new();
        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            string name = string.Empty;
            string cookieValue = string.Empty;
            string domain = string.Empty;
            string path = "/";
            string sameSite = string.Empty;
            double expires = -1;
            bool httpOnly = false;
            bool secure = false;

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual("name"u8))
                {
                    name = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("value"u8))
                {
                    cookieValue = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("domain"u8))
                {
                    domain = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("path"u8))
                {
                    path = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("sameSite"u8))
                {
                    sameSite = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("expires"u8))
                {
                    cursor.TryGetDouble(out expires);
                }
                else if (property.SequenceEqual("httpOnly"u8))
                {
                    cursor.TryGetBoolean(out httpOnly);
                }
                else if (property.SequenceEqual("secure"u8))
                {
                    cursor.TryGetBoolean(out secure);
                }
                else if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            cookies.Add(new CdpCookie
            {
                Name = name,
                Value = cookieValue,
                Domain = domain,
                Path = path,
                SameSite = sameSite,
                Expires = expires,
                HttpOnly = httpOnly,
                Secure = secure,
            });
        }

        value = new CookiesResult { Cookies = cookies };
        return true;
    }

    private static string ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            return string.Empty;
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }
}

/// <summary>Parameters of <c>Network.setCookie</c>.</summary>
public readonly struct SetCookieParams : ICdpCommandParams
{
    private readonly string _name;
    private readonly string _value;
    private readonly string _url;

    public SetCookieParams(string name, string value, string url)
    {
        _name = name;
        _value = value;
        _url = url;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("name"u8);
        writer.StringValue(_name);
        writer.Name("value"u8);
        writer.StringValue(_value);
        writer.Name("url"u8);
        writer.StringValue(_url);
    }
}

/// <summary>Result of <c>Network.setCookie</c>.</summary>
public readonly struct SetCookieResult : ICdpResult<SetCookieResult>
{
    public bool Success { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out SetCookieResult value)
    {
        value = new SetCookieResult { Success = true };
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return true;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("success"u8))
            {
                cursor.TryGetBoolean(out bool success);
                value = new SetCookieResult { Success = success };
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return true;
    }
}
