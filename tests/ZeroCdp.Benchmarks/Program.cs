using System.Diagnostics;
using System.Runtime.CompilerServices;
using ZeroCdp.Browser;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Transport;

namespace ZeroCdp.Benchmarks;

/// <summary>
/// Hand-rolled micro benchmarks, kept dependency free so the numbers reproduce with a plain
/// <c>dotnet run -c Release</c> on any machine.
/// </summary>
public static class Program
{
    private static readonly byte[] EventFrame =
        """{"method":"Network.responseReceived","sessionId":"A1B2C3","params":{"requestId":"1000.2","type":"Document","response":{"url":"https://example.com/","status":200,"headers":{}}}}"""u8
        .ToArray();

    private static readonly byte[] SmallObject = """{"a":"hello world"}"""u8.ToArray();
    private static readonly byte[] SharedOnly = """{"id":7,"result":{"value":42}}"""u8.ToArray();

    private static int s_sink;

    public static async Task Main()
    {
        const int iterations = 2_000_000;

        Run("parse: one string", iterations, static () =>
        {
            JsonCursor cursor = new(SmallObject);
            if (cursor.TryEnterObject() && cursor.TryNextProperty(out _) && cursor.TryReadString(out _, out _))
            {
                s_sink++;
            }
        });

        Run("parse: skip value", iterations, static () =>
        {
            JsonCursor cursor = new(SmallObject);
            if (cursor.TryEnterObject() && cursor.TryNextProperty(out _) && cursor.SkipValue())
            {
                s_sink++;
            }
        });

        Run("parse: full envelope", iterations, static () =>
        {
            JsonCursor cursor = new(SharedOnly);
            if (!cursor.TryEnterObject())
            {
                return;
            }

            while (cursor.TryNextProperty(out _))
            {
                if (!cursor.SkipValue())
                {
                    break;
                }
            }
        });

        FrameDispatcher dispatcher = new(runContinuationsAsynchronously: false) { EventSink = new NullSink() };
        Run("dispatch: event frame", iterations, () => dispatcher.Dispatch(EventFrame));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            dispatcher.Dispatch(EventFrame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"allocated     : {allocated} B over {iterations:N0} dispatches");
        GC.KeepAlive(s_sink);

        await RunSessionDispatchAsync().ConfigureAwait(false);
        await RunCommandRoundTripsAsync().ConfigureAwait(false);
        await RunLocatorLatencyAsync().ConfigureAwait(false);
    }

    /// <summary>What a click and a shadow-piercing lookup actually cost end to end.</summary>
    private static async Task RunLocatorLatencyAsync()
    {
        if (BrowserLocator.Find() is null)
        {
            Console.WriteLine("locator       : skipped (no browser installed)");
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), "zerocdp-bench-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(
            path,
            """
            <!doctype html><html><head><title>bench</title></head><body>
              <button id="b" onclick="void 0">go</button>
              <div id="host"></div>
              <script>
                const root = document.getElementById('host').attachShadow({ mode: 'closed' });
                root.innerHTML = '<button id="inner">i</button>';
              </script>
            </body></html>
            """);

        try
        {
            await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });
            CdpPage page = await browser.Browser.NewPageAsync();
            await page.GotoAsync(new Uri(path).AbsoluteUri);

            Locator button = page.Locator("#b");
            Locator shadow = page.Locator("#inner", pierceShadowDom: true);
            Locator autoShadow = page.Locator("#inner");
            Locator semantic = page.GetByRole("button", "go");

            for (int i = 0; i < 20; i++)
            {
                await button.ClickAsync().ConfigureAwait(false);
                await shadow.CountAsync().ConfigureAwait(false);
                await autoShadow.CountAsync().ConfigureAwait(false);
                await semantic.CountAsync().ConfigureAwait(false);
                await page.EvaluateAsync("1 + 1").ConfigureAwait(false);
            }

            Console.WriteLine($"locator click : {await MeasureAsync(200, () => button.ClickAsync()).ConfigureAwait(false),8:F1} us/op");
            Console.WriteLine($"selector query: {await MeasureAsync(500, async () => _ = await button.CountAsync()).ConfigureAwait(false),8:F1} us/op");
            Console.WriteLine($"shadow lookup : {await MeasureAsync(200, async () => _ = await shadow.CountAsync()).ConfigureAwait(false),8:F1} us/op");
            Console.WriteLine($"auto shadow   : {await MeasureAsync(200, async () => _ = await autoShadow.CountAsync()).ConfigureAwait(false),8:F1} us/op");
            Console.WriteLine($"semantic role : {await MeasureAsync(200, async () => _ = await semantic.CountAsync()).ConfigureAwait(false),8:F1} us/op");
            Console.WriteLine($"evaluate      : {await MeasureAsync(500, async () => _ = await page.EvaluateAsync("1 + 1")).ConfigureAwait(false),8:F1} us/op");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<double> MeasureAsync(int count, Func<ValueTask> body)
    {
        double best = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                await body().ConfigureAwait(false);
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMicroseconds / count);
        }

        return best;
    }

    /// <summary>Full path: frame -&gt; session demux -&gt; per-session sink.</summary>
    private static async Task RunSessionDispatchAsync()
    {
        using FakeTransport transport = new();
        await using CdpConnection connection = CdpConnection.Create(transport);
        SessionRouter router = new(connection) { SessionAttached = session => session.EventSink = s_sinkSink };
        connection.EventSink = router;

        byte[] attached = """{"sessionId":"A1B2C3D4E5","targetInfo":{"targetId":"T1","type":"page"},"waitingForDebugger":false}"""u8.ToArray();
        router.OnEvent(default, "Target.attachedToTarget"u8, attached);

        byte[] frame = """{"method":"Network.responseReceived","sessionId":"A1B2C3D4E5","params":{"requestId":"1000.2","type":"Document","response":{"url":"https://example.com/","status":200,"headers":{}}}}"""u8.ToArray();

        const int iterations = 2_000_000;
        Run("dispatch: session event", iterations, () => connection.DispatchForTest(frame));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            connection.DispatchForTest(frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"session alloc : {allocated} B over {iterations:N0} routed events");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Bump() => s_sink++;

    private static readonly NullSink s_sinkSink = new();

    private sealed class FakeTransport : ICdpTransport, IDisposable
    {
        private readonly System.IO.Pipelines.Pipe _pipe = new();

        public System.IO.Pipelines.PipeReader Input => _pipe.Reader;

        public string Describe => "fake";

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => default;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void Dispose()
        {
            _pipe.Reader.Complete();
            _pipe.Writer.Complete();
        }
    }

    /// <summary>End-to-end latency of a real command, including the browser's own work.</summary>
    private static async Task RunCommandRoundTripsAsync()
    {
        if (BrowserLocator.Find() is null)
        {
            Console.WriteLine("round trip    : skipped (no browser installed)");
            return;
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
        });

        Console.WriteLine($"transport     : {browser.Connection.TransportDescription}");

        const int warmup = 200;
        const int count = 2_000;

        for (int i = 0; i < warmup; i++)
        {
            await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8).ConfigureAwait(false);
        }

        double best = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8).ConfigureAwait(false);
            }

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMicroseconds / count);
        }

        Console.WriteLine($"Browser.getVersion    : {best,8:F1} us/round-trip");
    }

    private static void Run(string name, int iterations, Action body)
    {
        for (int i = 0; i < 200_000; i++)
        {
            body();
        }

        double best = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++)
            {
                body();
            }

            double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            best = Math.Min(best, ns);
        }

        Console.WriteLine($"{name,-22}: {best,8:F1} ns/op");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Touch() => s_sink++;

    private sealed class NullSink : ICdpEventSink
    {
        public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8) => Bump();
    }
}
