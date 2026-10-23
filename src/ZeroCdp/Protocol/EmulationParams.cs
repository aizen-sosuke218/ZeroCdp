using System.Text;
using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Emulation.setDeviceMetricsOverride</c>.</summary>
public readonly struct SetDeviceMetricsOverrideParams : ICdpCommandParams
{
    private readonly int _width;
    private readonly int _height;
    private readonly double _deviceScaleFactor;
    private readonly bool _mobile;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    public SetDeviceMetricsOverrideParams(
        int width,
        int height,
        double deviceScaleFactor,
        bool mobile,
        int screenWidth = 0,
        int screenHeight = 0)
    {
        _width = width;
        _height = height;
        _deviceScaleFactor = deviceScaleFactor;
        _mobile = mobile;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("width"u8);
        writer.Int32Value(_width);
        writer.Name("height"u8);
        writer.Int32Value(_height);
        writer.Name("deviceScaleFactor"u8);
        writer.DoubleValue(_deviceScaleFactor);
        writer.Name("mobile"u8);
        writer.BoolValue(_mobile);

        if (_screenWidth > 0)
        {
            writer.Name("screenWidth"u8);
            writer.Int32Value(_screenWidth);
        }

        if (_screenHeight > 0)
        {
            writer.Name("screenHeight"u8);
            writer.Int32Value(_screenHeight);
        }
    }
}

/// <summary>Parameters of <c>Emulation.setTouchEmulationEnabled</c>.</summary>
public readonly struct SetTouchEmulationEnabledParams : ICdpCommandParams
{
    private readonly bool _enabled;
    private readonly int _maxTouchPoints;

    public SetTouchEmulationEnabledParams(bool enabled, int maxTouchPoints = 1)
    {
        _enabled = enabled;
        _maxTouchPoints = maxTouchPoints;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("enabled"u8);
        writer.BoolValue(_enabled);
        writer.Name("maxTouchPoints"u8);
        writer.Int32Value(_maxTouchPoints);
    }
}

/// <summary>Parameters of <c>Emulation.setUserAgentOverride</c>.</summary>
public readonly struct SetUserAgentOverrideParams : ICdpCommandParams
{
    private readonly string _userAgent;
    private readonly string? _acceptLanguage;

    public SetUserAgentOverrideParams(string userAgent, string? acceptLanguage = null)
    {
        _userAgent = userAgent;
        _acceptLanguage = acceptLanguage;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("userAgent"u8);
        writer.StringValue(_userAgent);

        if (!string.IsNullOrEmpty(_acceptLanguage))
        {
            writer.Name("acceptLanguage"u8);
            writer.StringValue(_acceptLanguage);
        }
    }
}

/// <summary>Parameters of <c>Emulation.setLocaleOverride</c>.</summary>
public readonly struct SetLocaleOverrideParams : ICdpCommandParams
{
    private readonly string _locale;

    public SetLocaleOverrideParams(string locale) => _locale = locale;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("locale"u8);
        writer.StringValue(_locale);
    }
}

/// <summary>Parameters of <c>Emulation.setTimezoneOverride</c>.</summary>
public readonly struct SetTimezoneOverrideParams : ICdpCommandParams
{
    private readonly string _timezoneId;

    public SetTimezoneOverrideParams(string timezoneId) => _timezoneId = timezoneId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("timezoneId"u8);
        writer.StringValue(_timezoneId);
    }
}

/// <summary>A CSS media feature used by <c>Emulation.setEmulatedMedia</c>.</summary>
public readonly struct EmulatedMediaFeature
{
    public EmulatedMediaFeature(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}

/// <summary>Parameters of <c>Emulation.setEmulatedMedia</c>.</summary>
public readonly struct SetEmulatedMediaParams : ICdpCommandParams
{
    private readonly string? _media;
    private readonly IReadOnlyList<EmulatedMediaFeature>? _features;

    public SetEmulatedMediaParams(string? media, IReadOnlyList<EmulatedMediaFeature>? features = null)
    {
        _media = media;
        _features = features;
    }

    public void Write(ref CommandWriter writer)
    {
        if (!string.IsNullOrEmpty(_media))
        {
            writer.Name("media"u8);
            writer.StringValue(_media);
        }

        if (_features is not null)
        {
            writer.Name("features"u8);
            writer.Value("["u8);
            for (int i = 0; i < _features.Count; i++)
            {
                if (i > 0)
                {
                    writer.Value(","u8);
                }

                writer.BeginObject();
                writer.Name("name"u8);
                writer.StringValue(_features[i].Name);
                writer.Name("value"u8);
                writer.StringValue(_features[i].Value);
                writer.EndObject();
            }

            writer.Value("]"u8);
        }
    }
}

/// <summary>Parameters of <c>Network.setExtraHTTPHeaders</c>.</summary>
public readonly struct SetExtraHttpHeadersParams : ICdpCommandParams
{
    private readonly IReadOnlyList<CdpHeader> _headers;
    private readonly string? _browserContextId;

    public SetExtraHttpHeadersParams(IReadOnlyList<CdpHeader> headers, string? browserContextId = null)
    {
        _headers = headers;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        CdpHeader.WriteObject(ref writer, "headers"u8, _headers);

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}

/// <summary>Parameters of <c>Page.setDocumentContent</c>.</summary>
public readonly struct SetDocumentContentParams : ICdpCommandParams
{
    private readonly string _frameId;
    private readonly string _html;

    public SetDocumentContentParams(string frameId, string html)
    {
        _frameId = frameId;
        _html = html;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("frameId"u8);
        writer.StringValue(_frameId);
        writer.Name("html"u8);
        writer.StringValue(_html);
    }
}

/// <summary>Parameters of <c>Page.reload</c>.</summary>
public readonly struct ReloadParams : ICdpCommandParams
{
    private readonly bool _ignoreCache;

    public ReloadParams(bool ignoreCache = false) => _ignoreCache = ignoreCache;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("ignoreCache"u8);
        writer.BoolValue(_ignoreCache);
    }
}

/// <summary>One entry in the browser's navigation history.</summary>
public sealed class NavigationEntry
{
    public int Id { get; init; }

    public string Url { get; init; } = string.Empty;

    public string UserTypedUrl { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string TransitionType { get; init; } = string.Empty;
}

/// <summary>Result of <c>Page.getNavigationHistory</c>.</summary>
public readonly struct NavigationHistoryResult : ICdpResult<NavigationHistoryResult>
{
    public int CurrentIndex { get; init; }

    public IReadOnlyList<NavigationEntry> Entries { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out NavigationHistoryResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        int currentIndex = -1;
        List<NavigationEntry> entries = new();

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("currentIndex"u8))
            {
                cursor.TryGetInt32(out currentIndex);
            }
            else if (property.SequenceEqual("entries"u8))
            {
                if (!cursor.TryEnterArray())
                {
                    return false;
                }

                while (cursor.TryNextArrayElement())
                {
                    if (!TryParseEntry(ref cursor, out NavigationEntry? entry))
                    {
                        return false;
                    }

                    entries.Add(entry);
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new NavigationHistoryResult { CurrentIndex = currentIndex, Entries = entries };
        return true;
    }

    private static bool TryParseEntry(ref JsonCursor cursor, out NavigationEntry entry)
    {
        entry = new NavigationEntry();
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        int id = 0;
        string url = string.Empty;
        string userTypedUrl = string.Empty;
        string title = string.Empty;
        string transitionType = string.Empty;

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
        {
            if (property.SequenceEqual("id"u8))
            {
                cursor.TryGetInt32(out id);
            }
            else if (property.SequenceEqual("url"u8))
            {
                url = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("userTypedURL"u8))
            {
                userTypedUrl = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("title"u8))
            {
                title = CdpHeader.ReadString(ref cursor);
            }
            else if (property.SequenceEqual("transitionType"u8))
            {
                transitionType = CdpHeader.ReadString(ref cursor);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        entry = new NavigationEntry
        {
            Id = id,
            Url = url,
            UserTypedUrl = userTypedUrl,
            Title = title,
            TransitionType = transitionType,
        };
        return true;
    }
}

/// <summary>Parameters of <c>Page.navigateToHistoryEntry</c>.</summary>
public readonly struct NavigateToHistoryEntryParams : ICdpCommandParams
{
    private readonly int _entryId;

    public NavigateToHistoryEntryParams(int entryId) => _entryId = entryId;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("entryId"u8);
        writer.Int32Value(_entryId);
    }
}

/// <summary>Parameters of <c>Page.printToPDF</c>.</summary>
public readonly struct PrintToPdfParams : ICdpCommandParams
{
    private readonly bool _printBackground;
    private readonly bool _landscape;
    private readonly string? _format;
    private readonly string? _width;
    private readonly string? _height;
    private readonly string? _marginTop;
    private readonly string? _marginRight;
    private readonly string? _marginBottom;
    private readonly string? _marginLeft;
    private readonly double _scale;
    private readonly string? _pageRanges;
    private readonly bool _preferCSSPageSize;
    private readonly bool _displayHeaderFooter;
    private readonly string? _headerTemplate;
    private readonly string? _footerTemplate;

    public PrintToPdfParams(
        bool printBackground = false,
        bool landscape = false,
        string? format = null,
        string? width = null,
        string? height = null,
        string? marginTop = null,
        string? marginRight = null,
        string? marginBottom = null,
        string? marginLeft = null,
        double scale = 1,
        string? pageRanges = null,
        bool preferCSSPageSize = false,
        bool displayHeaderFooter = false,
        string? headerTemplate = null,
        string? footerTemplate = null)
    {
        _printBackground = printBackground;
        _landscape = landscape;
        _format = format;
        _width = width;
        _height = height;
        _marginTop = marginTop;
        _marginRight = marginRight;
        _marginBottom = marginBottom;
        _marginLeft = marginLeft;
        _scale = scale;
        _pageRanges = pageRanges;
        _preferCSSPageSize = preferCSSPageSize;
        _displayHeaderFooter = displayHeaderFooter;
        _headerTemplate = headerTemplate;
        _footerTemplate = footerTemplate;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("printBackground"u8);
        writer.BoolValue(_printBackground);
        writer.Name("landscape"u8);
        writer.BoolValue(_landscape);

        WriteOptionalString(ref writer, "format"u8, _format);
        WriteOptionalString(ref writer, "width"u8, _width);
        WriteOptionalString(ref writer, "height"u8, _height);

        if (!string.IsNullOrEmpty(_marginTop)
            || !string.IsNullOrEmpty(_marginRight)
            || !string.IsNullOrEmpty(_marginBottom)
            || !string.IsNullOrEmpty(_marginLeft))
        {
            writer.Name("marginTop"u8);
            writer.StringValue(_marginTop ?? "0");
            writer.Name("marginBottom"u8);
            writer.StringValue(_marginBottom ?? "0");
            writer.Name("marginLeft"u8);
            writer.StringValue(_marginLeft ?? "0");
            writer.Name("marginRight"u8);
            writer.StringValue(_marginRight ?? "0");
        }

        if (_scale > 0 && _scale != 1)
        {
            writer.Name("scale"u8);
            writer.DoubleValue(_scale);
        }

        WriteOptionalString(ref writer, "pageRanges"u8, _pageRanges);

        if (_preferCSSPageSize)
        {
            writer.Name("preferCSSPageSize"u8);
            writer.BoolValue(true);
        }

        if (_displayHeaderFooter)
        {
            writer.Name("displayHeaderFooter"u8);
            writer.BoolValue(true);
        }

        WriteOptionalString(ref writer, "headerTemplate"u8, _headerTemplate);
        WriteOptionalString(ref writer, "footerTemplate"u8, _footerTemplate);
    }

    private static void WriteOptionalString(
        ref CommandWriter writer,
        ReadOnlySpan<byte> name,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.Name(name);
        writer.StringValue(value);
    }
}

/// <summary>Result of <c>Page.printToPDF</c> with the base64 payload decoded.</summary>
public readonly struct PdfResult : ICdpResult<PdfResult>
{
    public ReadOnlyMemory<byte> Data { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out PdfResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("data"u8))
            {
                if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
                {
                    return false;
                }

                if (escaped)
                {
                    byte[] buffer = new byte[raw.Length];
                    if (!JsonUtf8.TryUnescape(raw, buffer, out int written))
                    {
                        return false;
                    }

                    return TryDecode(buffer.AsSpan(0, written), out value);
                }

                return TryDecode(raw, out value);
            }

            if (!cursor.SkipValue())
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryDecode(ReadOnlySpan<byte> raw, out PdfResult value)
    {
        value = default;
        int max = System.Buffers.Text.Base64.GetMaxDecodedFromUtf8Length(raw.Length);
        byte[] decoded = new byte[max];
        if (System.Buffers.Text.Base64.DecodeFromUtf8(raw, decoded, out _, out int written)
            != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        value = new PdfResult { Data = decoded.AsMemory(0, written) };
        return true;
    }
}

/// <summary>Parameters of <c>Page.setBypassCSP</c>.</summary>
public readonly struct SetBypassCspParams : ICdpCommandParams
{
    private readonly bool _enabled;

    public SetBypassCspParams(bool enabled) => _enabled = enabled;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("enabled"u8);
        writer.BoolValue(_enabled);
    }
}

/// <summary>Parameters of <c>Emulation.setGeolocationOverride</c>.</summary>
public readonly struct SetGeolocationOverrideParams : ICdpCommandParams
{
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly double _accuracy;

    public SetGeolocationOverrideParams(double latitude, double longitude, double accuracy = 1)
    {
        _latitude = latitude;
        _longitude = longitude;
        _accuracy = accuracy;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("latitude"u8);
        writer.DoubleValue(_latitude);
        writer.Name("longitude"u8);
        writer.DoubleValue(_longitude);
        writer.Name("accuracy"u8);
        writer.DoubleValue(_accuracy);
    }
}

/// <summary>Parameters of <c>Emulation.setScriptExecutionDisabled</c>.</summary>
public readonly struct SetScriptExecutionDisabledParams : ICdpCommandParams
{
    private readonly bool _value;

    public SetScriptExecutionDisabledParams(bool value) => _value = value;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("value"u8);
        writer.BoolValue(_value);
    }
}
