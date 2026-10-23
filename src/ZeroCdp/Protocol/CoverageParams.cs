using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Profiler.startPreciseCoverage</c>.</summary>
public readonly struct StartPreciseCoverageParams : ICdpCommandParams
{
    private readonly bool _callCount;
    private readonly bool _detailed;

    public StartPreciseCoverageParams(bool callCount = true, bool detailed = true)
    {
        _callCount = callCount;
        _detailed = detailed;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("callCount"u8);
        writer.BoolValue(_callCount);
        writer.Name("detailed"u8);
        writer.BoolValue(_detailed);
    }
}

/// <summary>A protocol result kept as raw JSON for domains whose payload is an open array.</summary>
public readonly struct RawJsonResult : ICdpResult<RawJsonResult>
{
    public ReadOnlyMemory<byte> Json { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> resultUtf8, out RawJsonResult value)
    {
        value = new RawJsonResult { Json = resultUtf8.ToArray() };
        return true;
    }
}
