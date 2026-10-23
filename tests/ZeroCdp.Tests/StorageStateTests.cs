using System.Diagnostics.CodeAnalysis;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

[SuppressMessage("Design", "CA1001", Justification = "xUnit calls DisposeAsync through IAsyncLifetime.")]
public sealed class StorageStateTests : IAsyncLifetime
{
    private LocalHttpServer? _server;

    public Task InitializeAsync()
    {
        _server = new LocalHttpServer
        {
            Handler = _ => new TestHttpResponse
            {
                Body = "<!doctype html><title>state</title><p id='ready'>ready</p>",
            },
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Storage_state_round_trips_cookies_and_local_storage()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        BrowserContext source = await browser.Browser.NewContextAsync();
        CdpPage page = await source.NewPageAsync();
        await page.GotoAsync(_server!.Url("/state"));
        await page.EvaluateAsync(
            "document.cookie = 'session=abc; path=/'; localStorage.setItem('one', '1'); localStorage.setItem('two', '2');");

        StorageState snapshot = await source.StorageStateAsync();
        Assert.Contains(snapshot.Cookies, cookie => cookie.Name == "session" && cookie.Value == "abc");

        OriginStorageState origin = Assert.Single(snapshot.Origins);
        Assert.Equal(_server.Url(string.Empty), origin.Origin);
        Assert.Equal(2, origin.LocalStorage.Count);
        Assert.Contains(origin.LocalStorage, item => item.Name == "one" && item.Value == "1");
        Assert.Contains(origin.LocalStorage, item => item.Name == "two" && item.Value == "2");

        BrowserContext target = await browser.Browser.NewContextAsync(
            new BrowserContextOptions { StorageState = snapshot });
        CdpPage imported = await target.NewPageAsync();
        await imported.GotoAsync(_server.Url("/state"));

        EvaluationResult cookie = await imported.EvaluateAsync("document.cookie");
        Assert.Contains("session=abc", cookie.Value.StringValue, StringComparison.Ordinal);

        EvaluationResult one = await imported.EvaluateAsync("localStorage.getItem('one')");
        Assert.Equal("1", one.Value.StringValue);
        EvaluationResult two = await imported.EvaluateAsync("localStorage.getItem('two')");
        Assert.Equal("2", two.Value.StringValue);

        StorageState roundTrip = await target.StorageStateAsync();
        OriginStorageState importedOrigin = Assert.Single(roundTrip.Origins);
        Assert.Contains(importedOrigin.LocalStorage, item => item.Name == "one" && item.Value == "1");
        Assert.Contains(importedOrigin.LocalStorage, item => item.Name == "two" && item.Value == "2");

        string file = Path.Combine(Path.GetTempPath(), "zerocdp-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await snapshot.SaveAsync(file);
            string json = await File.ReadAllTextAsync(file);
            Assert.Contains("\"cookies\"", json, StringComparison.Ordinal);
            Assert.Contains("\"origins\"", json, StringComparison.Ordinal);

            StorageState loaded = await StorageState.LoadAsync(file);
            Assert.Contains(loaded.Cookies, cookie => cookie.Name == "session" && cookie.Value == "abc");
            OriginStorageState loadedOrigin = Assert.Single(loaded.Origins);
            Assert.Equal(_server.Url(string.Empty), loadedOrigin.Origin);
            Assert.Contains(loadedOrigin.LocalStorage, item => item.Name == "one" && item.Value == "1");
            Assert.Contains(loadedOrigin.LocalStorage, item => item.Name == "two" && item.Value == "2");
        }
        finally
        {
            File.Delete(file);
        }

        await source.DisposeAsync();
        await target.DisposeAsync();
    }

    [Fact]
    public async Task Storage_state_round_trips_indexed_db()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });

        BrowserContext source = await browser.Browser.NewContextAsync();
        CdpPage page = await source.NewPageAsync();
        await page.GotoAsync(_server!.Url("/state"));

        EvaluationResult seeded = await page.EvaluateAsync(
            """
            (async () => {
              const request = req => new Promise((resolve, reject) => {
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error);
              });

              const db = await new Promise((resolve, reject) => {
                const open = indexedDB.open('auth-db', 2);
                open.onupgradeneeded = () => {
                  const database = open.result;
                  const sessions = database.createObjectStore('sessions', { keyPath: 'id' });
                  sessions.createIndex('byUser', 'user', { unique: false });
                  database.createObjectStore('kv');
                };
                open.onsuccess = () => resolve(open.result);
                open.onerror = () => reject(open.error);
              });

              await new Promise((resolve, reject) => {
                const transaction = db.transaction(['sessions', 'kv'], 'readwrite');
                transaction.objectStore('sessions').put({
                  id: 's1',
                  user: 'alice',
                  token: 'xyz',
                  n: 42,
                  nested: { ok: true }
                });
                transaction.objectStore('sessions').put({
                  id: 's2',
                  user: 'bob',
                  createdAt: new Date('2024-02-03T04:05:06.000Z'),
                  bytes: new Uint8Array([1, 2, 3])
                });
                transaction.objectStore('kv').put('v1', 'k1');
                transaction.oncomplete = () => resolve();
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
              });
              db.close();
              return true;
            })()
            """);
        Assert.False(seeded.Threw, seeded.ExceptionSummary);

        StorageState snapshot = await source.StorageStateAsync(
            new StorageStateOptions { IndexedDb = true });

        OriginStorageState origin = Assert.Single(snapshot.Origins);
        IndexedDbDatabase database = Assert.Single(origin.IndexedDb);
        Assert.Equal("auth-db", database.Name);
        Assert.Equal(2, database.Version);
        Assert.Equal(2, database.Stores.Count);

        IndexedDbStore sessionsStore = Assert.Single(
            database.Stores,
            store => store.Name == "sessions");
        Assert.Equal("id", sessionsStore.KeyPath);
        IndexedDbIndex index = Assert.Single(sessionsStore.Indexes);
        Assert.Equal("byUser", index.Name);
        Assert.Equal("user", index.KeyPath);
        Assert.Equal(2, sessionsStore.Records.Count);

        IndexedDbRecord plain = Assert.Single(
            sessionsStore.Records,
            record => record.Value is { } value
                && value.GetProperty("id").GetString() == "s1");
        Assert.Equal("alice", plain.Value!.Value.GetProperty("user").GetString());

        IndexedDbRecord encoded = Assert.Single(
            sessionsStore.Records,
            record => record.ValueEncoded is not null);
        Assert.Null(encoded.Value);

        IndexedDbStore kvStore = Assert.Single(
            database.Stores,
            store => store.Name == "kv");
        Assert.Null(kvStore.KeyPath);
        IndexedDbRecord kvRecord = Assert.Single(kvStore.Records);
        Assert.Equal("k1", kvRecord.Key!.Value.GetString());
        Assert.Equal("v1", kvRecord.Value!.Value.GetString());

        string file = Path.Combine(
            Path.GetTempPath(),
            "zerocdp-idb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await snapshot.SaveAsync(file);
            string json = await File.ReadAllTextAsync(file);
            Assert.Contains("\"indexedDB\"", json, StringComparison.Ordinal);

            StorageState loaded = await StorageState.LoadAsync(file);
            OriginStorageState loadedOrigin = Assert.Single(loaded.Origins);
            IndexedDbDatabase loadedDatabase = Assert.Single(loadedOrigin.IndexedDb);
            Assert.Equal("auth-db", loadedDatabase.Name);
            Assert.Equal(2, loadedDatabase.Version);

            BrowserContext target = await browser.Browser.NewContextAsync(
                new BrowserContextOptions { StorageState = loaded });
            CdpPage imported = await target.NewPageAsync();
            await imported.GotoAsync(_server.Url("/state"));

            EvaluationResult read = await imported.EvaluateAsync(
                """
                (async () => {
                  const request = req => new Promise((resolve, reject) => {
                    req.onsuccess = () => resolve(req.result);
                    req.onerror = () => reject(req.error);
                  });
                  const db = await request(indexedDB.open('auth-db'));
                  const get = (store, key) => new Promise((resolve, reject) => {
                    const transaction = db.transaction(store, 'readonly');
                    const req = transaction.objectStore(store).get(key);
                    req.onsuccess = () => resolve(req.result);
                    req.onerror = () => reject(req.error);
                  });
                  const s1 = await get('sessions', 's1');
                  const s2 = await get('sessions', 's2');
                  const kv = await get('kv', 'k1');
                  const indexes = [...db.transaction('sessions').objectStore('sessions').indexNames];
                  db.close();
                  return JSON.stringify({
                    s1Token: s1.token,
                    s1Nested: s1.nested.ok,
                    s2Date: s2.createdAt instanceof Date ? s2.createdAt.toISOString() : null,
                    s2Bytes: s2.bytes instanceof Uint8Array ? [...s2.bytes] : null,
                    kv,
                    indexes
                  });
                })()
                """);

            using System.Text.Json.JsonDocument result = System.Text.Json.JsonDocument.Parse(
                read.Value.StringValue ?? "{}");
            System.Text.Json.JsonElement root = result.RootElement;
            Assert.Equal("xyz", root.GetProperty("s1Token").GetString());
            Assert.True(root.GetProperty("s1Nested").GetBoolean());
            Assert.Equal(
                "2024-02-03T04:05:06.000Z",
                root.GetProperty("s2Date").GetString());
            Assert.Equal(3, root.GetProperty("s2Bytes").GetArrayLength());
            Assert.Equal("v1", root.GetProperty("kv").GetString());
            Assert.Contains(
                "byUser",
                root.GetProperty("indexes").EnumerateArray().Select(item => item.GetString()));

            await target.DisposeAsync();
        }
        finally
        {
            File.Delete(file);
        }

        await source.DisposeAsync();
    }
}
