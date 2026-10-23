using System.Globalization;
using System.Text;
using System.Text.Json;
using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="BrowserContext.StorageStateAsync(StorageStateOptions, CancellationToken)"/>.</summary>
public sealed class StorageStateOptions
{
    /// <summary>
    /// Also collect IndexedDB databases, object stores and records. This is opt-in because
    /// some applications keep large binary payloads in IndexedDB.
    /// </summary>
    public bool IndexedDb { get; init; }
}

/// <summary>One IndexedDB database captured in a storage state.</summary>
public sealed class IndexedDbDatabase
{
    public string Name { get; init; } = string.Empty;

    public long Version { get; init; }

    public IReadOnlyList<IndexedDbStore> Stores { get; init; } = Array.Empty<IndexedDbStore>();
}

/// <summary>One IndexedDB object store captured in a storage state.</summary>
public sealed class IndexedDbStore
{
    public string Name { get; init; } = string.Empty;

    public string? KeyPath { get; init; }

    public IReadOnlyList<string> KeyPathArray { get; init; } = Array.Empty<string>();

    public bool AutoIncrement { get; init; }

    public IReadOnlyList<IndexedDbIndex> Indexes { get; init; } = Array.Empty<IndexedDbIndex>();

    public IReadOnlyList<IndexedDbRecord> Records { get; init; } = Array.Empty<IndexedDbRecord>();
}

/// <summary>One IndexedDB index captured in a storage state.</summary>
public sealed class IndexedDbIndex
{
    public string Name { get; init; } = string.Empty;

    public string? KeyPath { get; init; }

    public IReadOnlyList<string> KeyPathArray { get; init; } = Array.Empty<string>();

    public bool MultiEntry { get; init; }

    public bool Unique { get; init; }
}

/// <summary>
/// One IndexedDB record. Exactly one of <see cref="Key"/> and <see cref="KeyEncoded"/> is normally
/// set when the store has an out-of-line key; the same applies to <see cref="Value"/> and
/// <see cref="ValueEncoded"/> when the value is not JSON-compatible.
/// </summary>
public sealed class IndexedDbRecord
{
    /// <summary>Raw JSON value for a JSON-compatible out-of-line key.</summary>
    public JsonElement? Key { get; init; }

    /// <summary>Playwright-compatible encoded value for a non-JSON out-of-line key.</summary>
    public JsonElement? KeyEncoded { get; init; }

    /// <summary>Raw JSON value for a JSON-compatible record value.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Playwright-compatible encoded value for a non-JSON record value.</summary>
    public JsonElement? ValueEncoded { get; init; }
}

/// <summary>Serializes and parses the IndexedDB portion of a Playwright storage state.</summary>
internal static class IndexedDbJson
{
    public static IReadOnlyList<IndexedDbDatabase> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<IndexedDbDatabase>();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return ParseArray(document.RootElement);
    }

    public static IReadOnlyList<IndexedDbDatabase> ParseArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("'indexedDB' must be an array.");
        }

        List<IndexedDbDatabase> databases = new();
        foreach (JsonElement databaseElement in element.EnumerateArray())
        {
            if (databaseElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Each IndexedDB database must be an object.");
            }

            List<IndexedDbStore> stores = new();
            if (databaseElement.TryGetProperty("stores", out JsonElement storesElement))
            {
                if (storesElement.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("IndexedDB database 'stores' must be an array.");
                }

                foreach (JsonElement storeElement in storesElement.EnumerateArray())
                {
                    stores.Add(ParseStore(storeElement));
                }
            }

            databases.Add(new IndexedDbDatabase
            {
                Name = ReadString(databaseElement, "name"),
                Version = ReadInt64(databaseElement, "version"),
                Stores = stores,
            });
        }

        return databases;
    }

    public static string Serialize(IReadOnlyList<IndexedDbDatabase> databases)
    {
        StringBuilder builder = new(256);
        Append(builder, databases);
        return builder.ToString();
    }

    public static void Append(StringBuilder builder, IReadOnlyList<IndexedDbDatabase> databases)
    {
        builder.Append('[');
        for (int i = 0; i < databases.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            IndexedDbDatabase database = databases[i];
            builder.Append("{\"name\":").Append(JsText.Quote(database.Name));
            builder.Append(",\"version\":").Append(
                database.Version.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"stores\":[");
            for (int j = 0; j < database.Stores.Count; j++)
            {
                if (j > 0)
                {
                    builder.Append(',');
                }

                AppendStore(builder, database.Stores[j]);
            }

            builder.Append("]}");
        }

        builder.Append(']');
    }

    public static string BuildRestoreScript(IReadOnlyList<IndexedDbDatabase> databases)
        => IndexedDbScript.RestoreTemplate.Replace(
            "__DATA__",
            JsText.Quote(Serialize(databases)),
            StringComparison.Ordinal);

    private static IndexedDbStore ParseStore(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Each IndexedDB object store must be an object.");
        }

        List<IndexedDbIndex> indexes = new();
        if (element.TryGetProperty("indexes", out JsonElement indexesElement))
        {
            if (indexesElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("IndexedDB store 'indexes' must be an array.");
            }

            foreach (JsonElement indexElement in indexesElement.EnumerateArray())
            {
                if (indexElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Each IndexedDB index must be an object.");
                }

                indexes.Add(new IndexedDbIndex
                {
                    Name = ReadString(indexElement, "name"),
                    KeyPath = ReadOptionalString(indexElement, "keyPath"),
                    KeyPathArray = ReadStringArray(indexElement, "keyPathArray"),
                    MultiEntry = ReadBoolean(indexElement, "multiEntry"),
                    Unique = ReadBoolean(indexElement, "unique"),
                });
            }
        }

        List<IndexedDbRecord> records = new();
        if (element.TryGetProperty("records", out JsonElement recordsElement))
        {
            if (recordsElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("IndexedDB store 'records' must be an array.");
            }

            foreach (JsonElement recordElement in recordsElement.EnumerateArray())
            {
                if (recordElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Each IndexedDB record must be an object.");
                }

                records.Add(new IndexedDbRecord
                {
                    Key = ReadOptionalElement(recordElement, "key"),
                    KeyEncoded = ReadOptionalElement(recordElement, "keyEncoded"),
                    Value = ReadOptionalElement(recordElement, "value"),
                    ValueEncoded = ReadOptionalElement(recordElement, "valueEncoded"),
                });
            }
        }

        return new IndexedDbStore
        {
            Name = ReadString(element, "name"),
            KeyPath = ReadOptionalString(element, "keyPath"),
            KeyPathArray = ReadStringArray(element, "keyPathArray"),
            AutoIncrement = ReadBoolean(element, "autoIncrement"),
            Indexes = indexes,
            Records = records,
        };
    }

    private static void AppendStore(StringBuilder builder, IndexedDbStore store)
    {
        builder.Append("{\"name\":").Append(JsText.Quote(store.Name));
        builder.Append(",\"records\":[");
        for (int i = 0; i < store.Records.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendRecord(builder, store.Records[i]);
        }

        builder.Append("],\"indexes\":[");
        for (int i = 0; i < store.Indexes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            IndexedDbIndex index = store.Indexes[i];
            builder.Append("{\"name\":").Append(JsText.Quote(index.Name));
            if (index.KeyPath is not null)
            {
                builder.Append(",\"keyPath\":").Append(JsText.Quote(index.KeyPath));
            }

            if (index.KeyPathArray.Count > 0)
            {
                builder.Append(",\"keyPathArray\":");
                AppendStringArray(builder, index.KeyPathArray);
            }

            builder.Append(",\"multiEntry\":").Append(index.MultiEntry ? "true" : "false");
            builder.Append(",\"unique\":").Append(index.Unique ? "true" : "false");
            builder.Append('}');
        }

        builder.Append("],\"autoIncrement\":").Append(store.AutoIncrement ? "true" : "false");
        if (store.KeyPath is not null)
        {
            builder.Append(",\"keyPath\":").Append(JsText.Quote(store.KeyPath));
        }

        if (store.KeyPathArray.Count > 0)
        {
            builder.Append(",\"keyPathArray\":");
            AppendStringArray(builder, store.KeyPathArray);
        }

        builder.Append('}');
    }

    private static void AppendRecord(StringBuilder builder, IndexedDbRecord record)
    {
        builder.Append('{');
        bool first = true;
        if (record.Key is { } key)
        {
            builder.Append("\"key\":").Append(key.GetRawText());
            first = false;
        }
        else if (record.KeyEncoded is { } keyEncoded)
        {
            builder.Append("\"keyEncoded\":").Append(keyEncoded.GetRawText());
            first = false;
        }

        if (record.Value is { } value)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append("\"value\":").Append(value.GetRawText());
        }
        else if (record.ValueEncoded is { } valueEncoded)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append("\"valueEncoded\":").Append(valueEncoded.GetRawText());
        }

        builder.Append('}');
    }

    private static void AppendStringArray(StringBuilder builder, IReadOnlyList<string> values)
    {
        builder.Append('[');
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(JsText.Quote(values[i]));
        }

        builder.Append(']');
    }

    private static JsonElement? ReadOptionalElement(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value) ? value.Clone() : null;

    private static string ReadString(JsonElement element, string propertyName)
        => ReadOptionalString(element, propertyName) ?? string.Empty;

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result))
        {
            return result;
        }

        return 0;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> result = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString() ?? string.Empty);
            }
        }

        return result;
    }
}

/// <summary>JavaScript used to collect and restore IndexedDB inside a document origin.</summary>
internal static class IndexedDbScript
{
    /// <summary>
    /// Returns <c>{ localStorage, indexedDB }</c>. The serializer follows Playwright's storage
    /// state wire shape: JSON-compatible values stay as <c>value</c>/<c>key</c>, while Dates,
    /// typed arrays, Maps, Sets and cycles use <c>valueEncoded</c>/<c>keyEncoded</c>.
    /// </summary>
    public const string CollectTemplate = """
        (async () => {
          const plain = value => {
            if (value === null || typeof value !== 'object') return false;
            const prototype = Object.getPrototypeOf(value);
            return prototype === Object.prototype || prototype === null;
          };

          const typedKind = value => {
            if (value instanceof Int8Array) return 'i8';
            if (value instanceof Uint8ClampedArray) return 'ui8c';
            if (value instanceof Uint8Array) return 'ui8';
            if (value instanceof Int16Array) return 'i16';
            if (value instanceof Uint16Array) return 'ui16';
            if (value instanceof Int32Array) return 'i32';
            if (value instanceof Uint32Array) return 'ui32';
            if (value instanceof Float32Array) return 'f32';
            if (value instanceof Float64Array) return 'f64';
            if (typeof BigInt64Array === 'function' && value instanceof BigInt64Array) return 'bi64';
            if (typeof BigUint64Array === 'function' && value instanceof BigUint64Array) return 'bui64';
            return null;
          };

          const toBase64 = bytes => {
            let binary = '';
            for (let i = 0; i < bytes.length; i += 0x8000) {
              binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
            }
            return btoa(binary);
          };

          const deepTrivial = (value, seen) => {
            if (value === null) return true;
            const kind = typeof value;
            if (kind === 'string' || kind === 'boolean') return true;
            if (kind === 'number') return Number.isFinite(value) && !Object.is(value, -0);
            if (kind !== 'object') return false;
            if (value instanceof Date || value instanceof URL || value instanceof RegExp
                || value instanceof Error || value instanceof Map || value instanceof Set
                || value instanceof ArrayBuffer || ArrayBuffer.isView(value)) return false;
            if (!Array.isArray(value) && !plain(value)) return false;
            if (seen.has(value)) return false;
            seen.add(value);
            let trivial = true;
            if (Array.isArray(value)) {
              for (let i = 0; i < value.length; i++) {
                if (!deepTrivial(value[i], seen)) { trivial = false; break; }
              }
            } else {
              for (const key of Object.keys(value)) {
                if (!deepTrivial(value[key], seen)) { trivial = false; break; }
              }
            }
            seen.delete(value);
            return trivial;
          };

          const encode = (value, state) => {
            if (value === null) return { v: 'null' };
            const kind = typeof value;
            if (kind === 'undefined' || kind === 'symbol') return { v: 'undefined' };
            if (kind === 'boolean' || kind === 'string') return value;
            if (kind === 'number') {
              if (Number.isNaN(value)) return { v: 'NaN' };
              if (value === Infinity) return { v: 'Infinity' };
              if (value === -Infinity) return { v: '-Infinity' };
              if (Object.is(value, -0)) return { v: '-0' };
              return value;
            }
            if (kind === 'bigint') return { bi: value.toString() };
            if (value instanceof Error) {
              const stack = value.stack && value.stack.startsWith(value.name + ': ' + value.message)
                ? value.stack
                : value.name + ': ' + value.message + '\n' + (value.stack || '');
              return { e: { n: value.name, m: value.message, s: stack } };
            }
            if (value instanceof Date) return { d: value.toJSON() };
            if (value instanceof URL) return { u: value.toJSON() };
            if (value instanceof RegExp) return { r: { p: value.source, f: value.flags } };
            const typed = typedKind(value);
            if (typed) {
              return { ta: { b: toBase64(new Uint8Array(value.buffer, value.byteOffset, value.byteLength)), k: typed } };
            }
            if (value instanceof ArrayBuffer) return { ab: { b: toBase64(new Uint8Array(value)) } };
            if (state.seen.has(value)) return { ref: state.seen.get(value) };
            const id = state.nextId++;
            if (value instanceof Map) {
              state.seen.set(value, id);
              const entries = [];
              for (const [key, item] of value) {
                entries.push({ k: encode(key, state), v: encode(item, state) });
              }
              return { m: entries, id };
            }
            if (value instanceof Set) {
              state.seen.set(value, id);
              const items = [];
              for (const item of value) items.push(encode(item, state));
              return { s: items, id };
            }
            if (Array.isArray(value)) {
              state.seen.set(value, id);
              const items = [];
              for (let i = 0; i < value.length; i++) items.push(encode(value[i], state));
              return { a: items, id };
            }
            if (kind === 'object') {
              state.seen.set(value, id);
              const entries = [];
              for (const key of Object.keys(value)) {
                if (key === '__proto__') continue;
                let item;
                try { item = value[key]; } catch (error) { continue; }
                entries.push({ k: key, v: encode(item, state) });
              }
              return { o: entries, id };
            }
            return { v: 'undefined' };
          };

          const field = (name, value) => {
            if (deepTrivial(value, new Set())) {
              const result = {};
              result[name] = value;
              return result;
            }
            const result = {};
            result[name + 'Encoded'] = encode(value, { seen: new Map(), nextId: 1 });
            return result;
          };

          const request = req => new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
          });

          const localStorageEntries = Object.keys(localStorage)
            .map(name => ({ name, value: localStorage.getItem(name) }));
          const databases = [];
          for (const info of await indexedDB.databases()) {
            if (!info.name) continue;
            const db = await request(indexedDB.open(info.name));
            try {
              const stores = [];
              const storeNames = [...db.objectStoreNames];
              if (storeNames.length > 0) {
                const transaction = db.transaction(storeNames, 'readonly');
                for (const storeName of storeNames) {
                  const objectStore = transaction.objectStore(storeName);
                  const keys = await request(objectStore.getAllKeys());
                  const records = [];
                  for (const key of keys) {
                    const record = {};
                    if (objectStore.keyPath === null) Object.assign(record, field('key', key));
                    const value = await request(objectStore.get(key));
                    Object.assign(record, field('value', value));
                    records.push(record);
                  }

                  const indexes = [];
                  for (const indexName of [...objectStore.indexNames]) {
                    const index = objectStore.index(indexName);
                    const info = {
                      name: index.name,
                      multiEntry: index.multiEntry,
                      unique: index.unique
                    };
                    if (typeof index.keyPath === 'string') info.keyPath = index.keyPath;
                    else if (Array.isArray(index.keyPath)) info.keyPathArray = index.keyPath;
                    indexes.push(info);
                  }

                  const store = {
                    name: objectStore.name,
                    records,
                    indexes,
                    autoIncrement: objectStore.autoIncrement
                  };
                  if (typeof objectStore.keyPath === 'string') store.keyPath = objectStore.keyPath;
                  else if (Array.isArray(objectStore.keyPath)) store.keyPathArray = objectStore.keyPath;
                  stores.push(store);
                }

                await new Promise((resolve, reject) => {
                  transaction.oncomplete = () => resolve();
                  transaction.onerror = () => reject(transaction.error);
                  transaction.onabort = () => reject(transaction.error);
                });
              }

              databases.push({
                name: info.name,
                version: info.version || db.version,
                stores
              });
            } finally {
              db.close();
            }
          }

          return JSON.stringify({ localStorage: localStorageEntries, indexedDB: databases });
        })()
        """;

    public const string RestoreTemplate = """
        (async () => {
          const databases = JSON.parse(__DATA__);
          const request = req => new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
          });

          const fromBase64 = text => {
            const binary = atob(text);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
            return bytes;
          };

          const typedConstructors = {
            i8: Int8Array, ui8: Uint8Array, ui8c: Uint8ClampedArray,
            i16: Int16Array, ui16: Uint16Array, i32: Int32Array, ui32: Uint32Array,
            f32: Float32Array, f64: Float64Array,
            bi64: typeof BigInt64Array === 'function' ? BigInt64Array : undefined,
            bui64: typeof BigUint64Array === 'function' ? BigUint64Array : undefined
          };

          const refs = new Map();
          const parse = value => {
            if (value === null || typeof value !== 'object') return value;
            if (Object.prototype.hasOwnProperty.call(value, 'ref')) return refs.get(value.ref);
            if (Object.prototype.hasOwnProperty.call(value, 'v')) {
              switch (value.v) {
                case 'undefined': return undefined;
                case 'null': return null;
                case 'NaN': return NaN;
                case 'Infinity': return Infinity;
                case '-Infinity': return -Infinity;
                case '-0': return -0;
                default: return undefined;
              }
            }
            if (Object.prototype.hasOwnProperty.call(value, 'd')) return new Date(value.d);
            if (Object.prototype.hasOwnProperty.call(value, 'u')) return new URL(value.u);
            if (Object.prototype.hasOwnProperty.call(value, 'bi')) return BigInt(value.bi);
            if (Object.prototype.hasOwnProperty.call(value, 'e')) {
              const error = new Error(value.e.m);
              error.name = value.e.n;
              error.stack = value.e.s;
              return error;
            }
            if (Object.prototype.hasOwnProperty.call(value, 'r')) return new RegExp(value.r.p, value.r.f);
            if (Object.prototype.hasOwnProperty.call(value, 'a')) {
              const result = [];
              if (value.id !== undefined) refs.set(value.id, result);
              for (const item of value.a) result.push(parse(item));
              return result;
            }
            if (Object.prototype.hasOwnProperty.call(value, 'o')) {
              const result = {};
              if (value.id !== undefined) refs.set(value.id, result);
              for (const entry of value.o) {
                if (entry.k === '__proto__') continue;
                result[entry.k] = parse(entry.v);
              }
              return result;
            }
            if (Object.prototype.hasOwnProperty.call(value, 'm')) {
              const result = new Map();
              if (value.id !== undefined) refs.set(value.id, result);
              for (const entry of value.m) result.set(parse(entry.k), parse(entry.v));
              return result;
            }
            if (Object.prototype.hasOwnProperty.call(value, 's')) {
              const result = new Set();
              if (value.id !== undefined) refs.set(value.id, result);
              for (const item of value.s) result.add(parse(item));
              return result;
            }
            if (Object.prototype.hasOwnProperty.call(value, 'ta')) {
              const Constructor = typedConstructors[value.ta.k];
              if (!Constructor) return undefined;
              return new Constructor(fromBase64(value.ta.b).buffer);
            }
            if (Object.prototype.hasOwnProperty.call(value, 'ab')) {
              return fromBase64(value.ab.b).buffer;
            }
            return value;
          };

          if (indexedDB.databases) {
            for (const info of await indexedDB.databases()) {
              if (!info.name) continue;
              await new Promise(resolve => {
                const deletion = indexedDB.deleteDatabase(info.name);
                deletion.onsuccess = () => resolve();
                deletion.onerror = () => resolve();
                deletion.onblocked = () => resolve();
              });
            }
          }

          for (const database of databases) {
            const open = indexedDB.open(database.name, database.version || undefined);
            open.onupgradeneeded = () => {
              const db = open.result;
              for (const store of database.stores || []) {
                const keyPath = store.keyPathArray && store.keyPathArray.length
                  ? store.keyPathArray
                  : store.keyPath;
                const options = { autoIncrement: !!store.autoIncrement };
                if (keyPath !== undefined) options.keyPath = keyPath;
                const objectStore = db.createObjectStore(store.name, options);
                for (const index of store.indexes || []) {
                  const indexKeyPath = index.keyPathArray && index.keyPathArray.length
                    ? index.keyPathArray
                    : index.keyPath;
                  if (indexKeyPath === undefined) continue;
                  objectStore.createIndex(index.name, indexKeyPath, {
                    unique: !!index.unique,
                    multiEntry: !!index.multiEntry
                  });
                }
              }
            };

            const db = await request(open);
            try {
              if (db.objectStoreNames.length === 0) continue;
              const transaction = db.transaction(db.objectStoreNames, 'readwrite');
              for (const store of database.stores || []) {
                const objectStore = transaction.objectStore(store.name);
                for (const record of store.records || []) {
                  const value = Object.prototype.hasOwnProperty.call(record, 'value')
                    ? record.value
                    : Object.prototype.hasOwnProperty.call(record, 'valueEncoded')
                      ? parse(record.valueEncoded)
                      : undefined;
                  if (Object.prototype.hasOwnProperty.call(record, 'key')) {
                    objectStore.add(value, record.key);
                  } else if (Object.prototype.hasOwnProperty.call(record, 'keyEncoded')) {
                    objectStore.add(value, parse(record.keyEncoded));
                  } else {
                    objectStore.add(value);
                  }
                }
              }

              await new Promise((resolve, reject) => {
                transaction.oncomplete = () => resolve();
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
              });
            } finally {
              db.close();
            }
          }

          return true;
        })()
        """;
}
