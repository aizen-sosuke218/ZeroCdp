using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Target.createBrowserContext</c>.</summary>
public readonly struct CreateBrowserContextParams : ICdpCommandParams
{
    private readonly bool _disposeOnDetach;
    private readonly string? _proxyServer;
    private readonly string? _proxyBypassList;

    public CreateBrowserContextParams(
        bool disposeOnDetach = true,
        string? proxyServer = null,
        string? proxyBypassList = null)
    {
        _disposeOnDetach = disposeOnDetach;
        _proxyServer = proxyServer;
        _proxyBypassList = proxyBypassList;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("disposeOnDetach"u8);
        writer.BoolValue(_disposeOnDetach);

        if (!string.IsNullOrEmpty(_proxyServer))
        {
            writer.Name("proxyServer"u8);
            writer.StringValue(_proxyServer);
        }

        if (!string.IsNullOrEmpty(_proxyBypassList))
        {
            writer.Name("proxyBypassList"u8);
            writer.StringValue(_proxyBypassList);
        }
    }
}

/// <summary>Result of <c>Target.createBrowserContext</c>.</summary>
public readonly struct BrowserContextCreated : ICdpResult<BrowserContextCreated>
{
    public string? BrowserContextId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out BrowserContextCreated value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("browserContextId"u8)
                && cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
            {
                value = new BrowserContextCreated { BrowserContextId = Encoding.UTF8.GetString(raw) };
                return true;
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Parameters of <c>Target.disposeBrowserContext</c>.</summary>
public readonly struct DisposeBrowserContextParams : ICdpCommandParams
{
    private readonly string _browserContextId;

    public DisposeBrowserContextParams(string browserContextId) => _browserContextId = browserContextId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("browserContextId"u8);
        writer.StringValue(_browserContextId);
    }
}

/// <summary>Parameters of <c>Storage.getCookies</c>.</summary>
public readonly struct StorageGetCookiesParams : ICdpCommandParams
{
    private readonly string? _browserContextId;

    public StorageGetCookiesParams(string? browserContextId = null) => _browserContextId = browserContextId;

    public void Write(ref CommandWriter writer)
    {
        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}

/// <summary>Parameters of <c>Storage.setCookies</c>.</summary>
public readonly struct StorageSetCookiesParams : ICdpCommandParams
{
    private readonly IReadOnlyList<CdpCookie> _cookies;
    private readonly string? _browserContextId;

    public StorageSetCookiesParams(IReadOnlyList<CdpCookie> cookies, string? browserContextId = null)
    {
        _cookies = cookies;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("cookies"u8);
        writer.Value("["u8);
        for (int i = 0; i < _cookies.Count; i++)
        {
            if (i > 0)
            {
                writer.Value(","u8);
            }

            CdpCookie cookie = _cookies[i];
            writer.BeginObject();
            WriteString(ref writer, "name"u8, cookie.Name);
            WriteString(ref writer, "value"u8, cookie.Value);

            if (!string.IsNullOrEmpty(cookie.Url))
            {
                WriteString(ref writer, "url"u8, cookie.Url);
            }
            else
            {
                WriteString(ref writer, "domain"u8, cookie.Domain);
                WriteString(ref writer, "path"u8, cookie.Path);
            }

            if (cookie.Expires >= 0)
            {
                writer.Name("expires"u8);
                writer.DoubleValue(cookie.Expires);
            }

            writer.Name("httpOnly"u8);
            writer.BoolValue(cookie.HttpOnly);
            writer.Name("secure"u8);
            writer.BoolValue(cookie.Secure);

            if (!string.IsNullOrEmpty(cookie.SameSite))
            {
                WriteString(ref writer, "sameSite"u8, cookie.SameSite);
            }

            writer.EndObject();
        }

        writer.Value("]"u8);

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }

    private static void WriteString(ref CommandWriter writer, ReadOnlySpan<byte> name, string value)
    {
        writer.Name(name);
        writer.StringValue(value);
    }
}

/// <summary>Parameters of <c>Storage.clearCookies</c>.</summary>
public readonly struct StorageClearCookiesParams : ICdpCommandParams
{
    private readonly string? _browserContextId;

    public StorageClearCookiesParams(string? browserContextId = null) => _browserContextId = browserContextId;

    public void Write(ref CommandWriter writer)
    {
        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}
