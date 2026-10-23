using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using Xunit;

namespace ZeroCdp.Tests;

public class FrameDispatcherTests
{
    [Fact]
    public async Task Completes_a_pending_command()
    {
        FrameDispatcher dispatcher = new(runContinuationsAsynchronously: false);
        PendingCommand<IntResult> slot = dispatcher.Register<IntResult>(7);
        ValueTask<IntResult> pending = slot.AsValueTask();

        dispatcher.Dispatch("""{"id":7,"result":{"value":42}}"""u8);

        IntResult result = await pending;
        Assert.Equal(42, result.Value);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task Surfaces_a_protocol_error()
    {
        FrameDispatcher dispatcher = new(runContinuationsAsynchronously: false);
        PendingCommand<IntResult> slot = dispatcher.Register<IntResult>(9);
        ValueTask<IntResult> pending = slot.AsValueTask();

        dispatcher.Dispatch("""{"id":9,"error":{"code":-32601,"message":"'Nope' wasn't found"}}"""u8);

        CdpCommandException error = await Assert.ThrowsAsync<CdpCommandException>(async () => await pending);
        Assert.Equal(-32601, error.Code);
        Assert.Equal("'Nope' wasn't found", error.Message);
    }

    [Fact]
    public void Routes_events_with_session_id_and_params()
    {
        CapturingSink sink = new();
        FrameDispatcher dispatcher = new(false) { EventSink = sink };

        dispatcher.Dispatch(
            """{"method":"Page.frameNavigated","sessionId":"ABC","params":{"frame":{"id":"F1"}}}"""u8);

        Assert.Equal(1, sink.Count);
        Assert.Equal("Page.frameNavigated", sink.Method);
        Assert.Equal("ABC", sink.SessionId);
        Assert.Equal("""{"frame":{"id":"F1"}}""", sink.Params);
    }

    [Fact]
    public void Ignores_responses_for_abandoned_commands()
    {
        FrameDispatcher dispatcher = new(false);
        dispatcher.Dispatch("""{"id":99,"result":{}}"""u8);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    /// <summary>
    /// The T0 gate: dispatching a steady stream of events must not allocate.
    /// This is asserted in CI so the transport/dispatch path cannot regress silently.
    /// </summary>
    [Fact]
    public void Dispatching_events_does_not_allocate()
    {
        CountingSink sink = new();
        FrameDispatcher dispatcher = new(false) { EventSink = sink };
        byte[] frame = """{"method":"Network.responseReceived","sessionId":"A1B2C3","params":{"requestId":"1000.2","type":"Document","response":{"url":"https://example.com/","status":200,"headers":{}}}}"""u8
            .ToArray();

        // Warm up: JIT, static init, dictionary growth.
        for (int i = 0; i < 1_000; i++)
        {
            dispatcher.Dispatch(frame);
        }

        sink.Count = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            dispatcher.Dispatch(frame);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000, sink.Count);
        Assert.True(allocated == 0, $"Dispatch allocated {allocated} bytes over 10k events.");
    }

    private sealed class CountingSink : ICdpEventSink
    {
        public int Count;

        public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
            => Count++;
    }

    private sealed class CapturingSink : ICdpEventSink
    {
        public int Count;
        public string Method = string.Empty;
        public string SessionId = string.Empty;
        public string Params = string.Empty;

        public void OnEvent(ReadOnlySpan<byte> sessionId, ReadOnlySpan<byte> method, ReadOnlySpan<byte> paramsUtf8)
        {
            Count++;
            Method = System.Text.Encoding.UTF8.GetString(method);
            SessionId = System.Text.Encoding.UTF8.GetString(sessionId);
            Params = System.Text.Encoding.UTF8.GetString(paramsUtf8);
        }
    }
}

public readonly struct IntResult : ICdpResult<IntResult>
{
    public int Value { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out IntResult value)
    {
        value = default;
        JsonCursor cursor = new(resultUtf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        int parsed = 0;
        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("value"u8))
            {
                if (!cursor.TryGetInt32(out parsed))
                {
                    return false;
                }
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        value = new IntResult { Value = parsed };
        return true;
    }
}
