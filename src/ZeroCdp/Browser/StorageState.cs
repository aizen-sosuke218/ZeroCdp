using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeroCdp.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>
/// Cookies and <c>localStorage</c> captured from a browser context. The shape mirrors
/// Playwright's storage-state object; serialization to JSON is left to the caller.
/// </summary>
public sealed class StorageState
{
    public IReadOnlyList<CdpCookie> Cookies { get; init; } = Array.Empty<CdpCookie>();

    public IReadOnlyList<OriginStorageState> Origins { get; init; } = Array.Empty<OriginStorageState>();

    /// <summary>
    /// Serializes the Playwright-compatible storage-state JSON shape. No reflection or
    /// serializer is involved, so this remains NativeAOT-safe.
    /// </summary>
    public string ToJson()
    {
        StringBuilder builder = new(256);
        builder.Append("{\"cookies\":[");

        for (int i = 0; i < Cookies.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            CdpCookie cookie = Cookies[i];
            builder.Append("{\"name\":").Append(JsText.Quote(cookie.Name));
            builder.Append(",\"value\":").Append(JsText.Quote(cookie.Value));
            builder.Append(",\"domain\":").Append(JsText.Quote(cookie.Domain));
            builder.Append(",\"path\":").Append(JsText.Quote(cookie.Path));
            builder.Append(",\"expires\":").Append(
                (double.IsFinite(cookie.Expires) ? cookie.Expires : -1)
                    .ToString("R", CultureInfo.InvariantCulture));
            builder.Append(",\"httpOnly\":").Append(cookie.HttpOnly ? "true" : "false");
            builder.Append(",\"secure\":").Append(cookie.Secure ? "true" : "false");

            if (cookie.SameSite.Length > 0)
            {
                builder.Append(",\"sameSite\":").Append(JsText.Quote(cookie.SameSite));
            }

            if (cookie.Url.Length > 0)
            {
                builder.Append(",\"url\":").Append(JsText.Quote(cookie.Url));
            }

            builder.Append('}');
        }

        builder.Append("],\"origins\":[");
        for (int i = 0; i < Origins.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            OriginStorageState origin = Origins[i];
            builder.Append("{\"origin\":").Append(JsText.Quote(origin.Origin));
            builder.Append(",\"localStorage\":[");
            for (int j = 0; j < origin.LocalStorage.Count; j++)
            {
                if (j > 0)
                {
                    builder.Append(',');
                }

                StorageEntry entry = origin.LocalStorage[j];
                builder.Append("{\"name\":").Append(JsText.Quote(entry.Name));
                builder.Append(",\"value\":").Append(JsText.Quote(entry.Value));
                builder.Append('}');
            }

            builder.Append(']');
            if (origin.IndexedDb.Count > 0)
            {
                builder.Append(",\"indexedDB\":");
                IndexedDbJson.Append(builder, origin.IndexedDb);
            }

            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>Parses the Playwright-compatible storage-state JSON shape.</summary>
    public static StorageState Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonCursor cursor = new(Encoding.UTF8.GetBytes(json));
        if (!cursor.TryEnterObject())
        {
            throw new FormatException("A storage state must be a JSON object.");
        }

        List<CdpCookie> cookies = new();
        List<OriginStorageState> origins = new();

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("cookies"u8))
            {
                ParseCookies(ref cursor, cookies);
            }
            else if (name.SequenceEqual("origins"u8))
            {
                ParseOrigins(ref cursor, origins);
            }
            else if (!cursor.SkipValue())
            {
                throw new FormatException("Malformed storage-state JSON.");
            }
        }

        return new StorageState { Cookies = cookies, Origins = origins };
    }

    /// <summary>Reads a storage-state file written by <see cref="SaveAsync"/>.</summary>
    public static async ValueTask<StorageState> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Writes the storage-state JSON to a file.</summary>
    public async ValueTask SaveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await File.WriteAllTextAsync(path, ToJson(), cancellationToken).ConfigureAwait(false);
    }

    private static void ParseCookies(ref JsonCursor cursor, List<CdpCookie> cookies)
    {
        if (!cursor.TryEnterArray())
        {
            throw new FormatException("'cookies' must be an array.");
        }

        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterObject())
            {
                throw new FormatException("Each cookie must be an object.");
            }

            string name = string.Empty;
            string value = string.Empty;
            string domain = string.Empty;
            string path = "/";
            string url = string.Empty;
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
                    value = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("domain"u8))
                {
                    domain = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("path"u8))
                {
                    path = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("url"u8))
                {
                    url = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("sameSite"u8))
                {
                    sameSite = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("expires"u8))
                {
                    if (!cursor.TryGetDouble(out expires))
                    {
                        throw new FormatException("Cookie 'expires' must be a number.");
                    }
                }
                else if (property.SequenceEqual("httpOnly"u8))
                {
                    if (!cursor.TryGetBoolean(out httpOnly))
                    {
                        throw new FormatException("Cookie 'httpOnly' must be a boolean.");
                    }
                }
                else if (property.SequenceEqual("secure"u8))
                {
                    if (!cursor.TryGetBoolean(out secure))
                    {
                        throw new FormatException("Cookie 'secure' must be a boolean.");
                    }
                }
                else if (!cursor.SkipValue())
                {
                    throw new FormatException("Malformed cookie object.");
                }
            }

            cookies.Add(new CdpCookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = path,
                Url = url,
                SameSite = sameSite,
                Expires = expires,
                HttpOnly = httpOnly,
                Secure = secure,
            });
        }
    }

    private static void ParseOrigins(ref JsonCursor cursor, List<OriginStorageState> origins)
    {
        if (!cursor.TryEnterArray())
        {
            throw new FormatException("'origins' must be an array.");
        }

        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterObject())
            {
                throw new FormatException("Each origin must be an object.");
            }

            string origin = string.Empty;
            List<StorageEntry> entries = new();
            List<IndexedDbDatabase> indexedDb = new();

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual("origin"u8))
                {
                    origin = ReadString(ref cursor);
                }
                else if (property.SequenceEqual("localStorage"u8))
                {
                    if (!cursor.TryEnterArray())
                    {
                        throw new FormatException("'localStorage' must be an array.");
                    }

                    while (cursor.TryNextArrayElement())
                    {
                        if (!cursor.TryEnterObject())
                        {
                            throw new FormatException("Each local storage entry must be an object.");
                        }

                        string entryName = string.Empty;
                        string entryValue = string.Empty;
                        while (cursor.TryNextProperty(out ReadOnlySpan<byte> entryProperty))
                        {
                            if (entryProperty.SequenceEqual("name"u8))
                            {
                                entryName = ReadString(ref cursor);
                            }
                            else if (entryProperty.SequenceEqual("value"u8))
                            {
                                entryValue = ReadString(ref cursor);
                            }
                            else if (!cursor.SkipValue())
                            {
                                throw new FormatException("Malformed local storage entry.");
                            }
                        }

                        entries.Add(new StorageEntry { Name = entryName, Value = entryValue });
                    }
                }
                else if (property.SequenceEqual("indexedDB"u8))
                {
                    int start = cursor.Position;
                    if (!cursor.SkipValue())
                    {
                        throw new FormatException("Malformed IndexedDB data.");
                    }

                    string json = Encoding.UTF8.GetString(cursor.Utf8[start..cursor.Position]);
                    indexedDb.AddRange(IndexedDbJson.Parse(json));
                }
                else if (!cursor.SkipValue())
                {
                    throw new FormatException("Malformed origin object.");
                }
            }

            origins.Add(
                new OriginStorageState
                {
                    Origin = origin,
                    LocalStorage = entries,
                    IndexedDb = indexedDb,
                });
        }
    }

    private static string ReadString(ref JsonCursor cursor)
    {
        if (!cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
        {
            throw new FormatException("Expected a JSON string.");
        }

        if (!escaped)
        {
            return Encoding.UTF8.GetString(raw);
        }

        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : throw new FormatException("Invalid JSON string escape.");
    }
}

/// <summary>Local storage entries for one security origin.</summary>
public sealed class OriginStorageState
{
    public string Origin { get; init; } = string.Empty;

    public IReadOnlyList<StorageEntry> LocalStorage { get; init; } = Array.Empty<StorageEntry>();

    /// <summary>IndexedDB databases for this origin, when the snapshot requested them.</summary>
    public IReadOnlyList<IndexedDbDatabase> IndexedDb { get; init; } = Array.Empty<IndexedDbDatabase>();
}

/// <summary>One key/value pair in <c>localStorage</c>.</summary>
public sealed class StorageEntry
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

internal static class StorageStateJson
{
    /// <summary>
    /// Parses the result of <c>JSON.stringify(Object.entries(localStorage))</c>:
    /// an array of <c>[key, value]</c> pairs.
    /// </summary>
    public static bool TryParseLocalStorageEntries(
        string? json,
        out IReadOnlyList<StorageEntry> entries)
    {
        entries = Array.Empty<StorageEntry>();
        if (string.IsNullOrEmpty(json))
        {
            return true;
        }

        List<StorageEntry> parsed = new();
        JsonCursor cursor = new(Encoding.UTF8.GetBytes(json));
        if (!cursor.TryEnterArray())
        {
            return false;
        }

        while (cursor.TryNextArrayElement())
        {
            if (!cursor.TryEnterArray())
            {
                return false;
            }

            if (!cursor.TryNextArrayElement() || !cursor.TryReadString(out ReadOnlySpan<byte> rawName, out bool nameEscaped))
            {
                return false;
            }

            string name = nameEscaped
                ? Unescape(rawName)
                : Encoding.UTF8.GetString(rawName);

            if (!cursor.TryNextArrayElement() || !cursor.TryReadString(out ReadOnlySpan<byte> rawValue, out bool valueEscaped))
            {
                return false;
            }

            string value = valueEscaped
                ? Unescape(rawValue)
                : Encoding.UTF8.GetString(rawValue);

            // Consume any extra array members and the inner closing bracket so the next outer
            // iteration sees the comma (or the outer closing bracket).
            while (cursor.TryNextArrayElement())
            {
                if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            parsed.Add(new StorageEntry { Name = name, Value = value });
        }

        entries = parsed;
        return true;
    }

    public static bool TryParseCollectedStorage(
        string? json,
        out IReadOnlyList<StorageEntry> entries,
        out IReadOnlyList<IndexedDbDatabase> indexedDb)
    {
        entries = Array.Empty<StorageEntry>();
        indexedDb = Array.Empty<IndexedDbDatabase>();
        if (string.IsNullOrEmpty(json))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            List<StorageEntry> parsedEntries = new();
            if (root.TryGetProperty("localStorage", out JsonElement localStorage)
                && localStorage.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in localStorage.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    parsedEntries.Add(
                        new StorageEntry
                        {
                            Name = item.TryGetProperty("name", out JsonElement name)
                                && name.ValueKind == JsonValueKind.String
                                ? name.GetString() ?? string.Empty
                                : string.Empty,
                            Value = item.TryGetProperty("value", out JsonElement value)
                                && value.ValueKind == JsonValueKind.String
                                ? value.GetString() ?? string.Empty
                                : string.Empty,
                        });
                }
            }

            if (root.TryGetProperty("indexedDB", out JsonElement databases)
                && databases.ValueKind == JsonValueKind.Array)
            {
                indexedDb = IndexedDbJson.ParseArray(databases);
            }

            entries = parsedEntries;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Unescape(ReadOnlySpan<byte> raw)
    {
        byte[] buffer = new byte[raw.Length];
        return JsonUtf8.TryUnescape(raw, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : string.Empty;
    }
}
