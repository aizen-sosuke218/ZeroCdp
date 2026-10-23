using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

/// <summary>Options for JavaScript coverage collection.</summary>
public sealed class JSCoverageOptions
{
    /// <summary>Reset coverage counters on every navigation. Defaults to true.</summary>
    public bool ResetOnNavigation { get; init; } = true;

    /// <summary>Include anonymous and internal scripts. Defaults to false.</summary>
    public bool ReportAnonymousScripts { get; init; }
}

/// <summary>A covered range produced by the V8 precise-coverage profiler.</summary>
public readonly struct JSCoverageRange
{
    public JSCoverageRange(int startOffset, int endOffset, int count)
    {
        StartOffset = startOffset;
        EndOffset = endOffset;
        Count = count;
    }

    public int StartOffset { get; }

    public int EndOffset { get; }

    public int Count { get; }
}

/// <summary>Coverage for one function inside a script.</summary>
public sealed class JSCoverageFunction
{
    public string FunctionName { get; init; } = string.Empty;

    public bool IsBlockCoverage { get; init; }

    public IReadOnlyList<JSCoverageRange> Ranges { get; init; } = Array.Empty<JSCoverageRange>();
}

/// <summary>Coverage for one JavaScript script.</summary>
public sealed class JSCoverageEntry
{
    public string Url { get; init; } = string.Empty;

    public string ScriptId { get; init; } = string.Empty;

    public IReadOnlyList<JSCoverageFunction> Functions { get; init; } = Array.Empty<JSCoverageFunction>();
}

/// <summary>CSS rule usage collected while rule tracking is enabled.</summary>
public readonly struct CssCoverageEntry
{
    public CssCoverageEntry(string styleSheetId, int startOffset, int endOffset, bool used)
    {
        StyleSheetId = styleSheetId;
        StartOffset = startOffset;
        EndOffset = endOffset;
        Used = used;
    }

    public string StyleSheetId { get; }

    public int StartOffset { get; }

    public int EndOffset { get; }

    public bool Used { get; }
}

public sealed partial class CdpPage
{
    private PageCoverage? _coverage;

    /// <summary>Opt-in JavaScript and CSS coverage collection for this page.</summary>
    public PageCoverage Coverage => _coverage ??= new PageCoverage(this);
}

/// <summary>Opt-in JavaScript and CSS coverage collection for a page.</summary>
public sealed class PageCoverage
{
    private readonly CdpPage _page;
    private int _jsActive;
    private int _cssActive;

    internal PageCoverage(CdpPage page) => _page = page;

    /// <summary>Starts V8 precise coverage. This enables the Profiler domain on the page session.</summary>
    public async ValueTask StartJSCoverageAsync(
        JSCoverageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new JSCoverageOptions();
        if (Interlocked.Exchange(ref _jsActive, 1) == 0)
        {
            await _page.Session
                .SendAsync<Unit>("Profiler.enable"u8, cancellationToken)
                .ConfigureAwait(false);
            await _page.Session
                .SendAsync<Unit, StartPreciseCoverageParams>(
                    "Profiler.startPreciseCoverage"u8,
                    new StartPreciseCoverageParams(callCount: true, detailed: true),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stops JavaScript coverage and returns the collected scripts.</summary>
    public async ValueTask<IReadOnlyList<JSCoverageEntry>> StopJSCoverageAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _jsActive, 0) == 0)
        {
            return [];
        }

        RawJsonResult raw = await _page.Session
            .SendAsync<RawJsonResult>("Profiler.takePreciseCoverage"u8, cancellationToken)
            .ConfigureAwait(false);
        await _page.Session
            .SendAsync<Unit>("Profiler.stopPreciseCoverage"u8, cancellationToken)
            .ConfigureAwait(false);
        await _page.Session
            .SendAsync<Unit>("Profiler.disable"u8, cancellationToken)
            .ConfigureAwait(false);
        return ParseScriptCoverage(raw.Json.Span);
    }

    /// <summary>Starts tracking which CSS rules were used.</summary>
    public async ValueTask StartCSSCoverageAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _cssActive, 1) == 0)
        {
            await _page.Session
                .SendAsync<Unit>("CSS.enable"u8, cancellationToken)
                .ConfigureAwait(false);
            await _page.Session
                .SendAsync<Unit>("CSS.startRuleUsageTracking"u8, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Stops CSS coverage and returns rule usage.</summary>
    public async ValueTask<IReadOnlyList<CssCoverageEntry>> StopCSSCoverageAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _cssActive, 0) == 0)
        {
            return [];
        }

        RawJsonResult raw = await _page.Session
            .SendAsync<RawJsonResult>("CSS.stopRuleUsageTracking"u8, cancellationToken)
            .ConfigureAwait(false);
        await _page.Session
            .SendAsync<Unit>("CSS.disable"u8, cancellationToken)
            .ConfigureAwait(false);
        return ParseCssCoverage(raw.Json.Span);
    }

    private static List<JSCoverageEntry> ParseScriptCoverage(ReadOnlySpan<byte> json)
    {
        JsonDocument document = JsonDocument.Parse(json.ToArray());
        try
        {
            if (!document.RootElement.TryGetProperty("result", out JsonElement scripts)
                || scripts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<JSCoverageEntry> entries = new();
            foreach (JsonElement script in scripts.EnumerateArray())
            {
                List<JSCoverageFunction> functions = new();
                if (script.TryGetProperty("functions", out JsonElement functionArray)
                    && functionArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement function in functionArray.EnumerateArray())
                    {
                        List<JSCoverageRange> ranges = new();
                        if (function.TryGetProperty("ranges", out JsonElement rangeArray)
                            && rangeArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement range in rangeArray.EnumerateArray())
                            {
                                ranges.Add(new JSCoverageRange(
                                    range.TryGetProperty("startOffset", out JsonElement start) ? start.GetInt32() : 0,
                                    range.TryGetProperty("endOffset", out JsonElement end) ? end.GetInt32() : 0,
                                    range.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 0));
                            }
                        }

                        functions.Add(new JSCoverageFunction
                        {
                            FunctionName = function.TryGetProperty("functionName", out JsonElement name)
                                ? name.GetString() ?? string.Empty
                                : string.Empty,
                            IsBlockCoverage = function.TryGetProperty("isBlockCoverage", out JsonElement block)
                                && block.GetBoolean(),
                            Ranges = ranges,
                        });
                    }
                }

                entries.Add(new JSCoverageEntry
                {
                    Url = script.TryGetProperty("url", out JsonElement url) ? url.GetString() ?? string.Empty : string.Empty,
                    ScriptId = script.TryGetProperty("scriptId", out JsonElement scriptId)
                        ? scriptId.GetString() ?? string.Empty
                        : string.Empty,
                    Functions = functions,
                });
            }

            return entries;
        }
        finally
        {
            document.Dispose();
        }
    }

    private static List<CssCoverageEntry> ParseCssCoverage(ReadOnlySpan<byte> json)
    {
        JsonDocument document = JsonDocument.Parse(json.ToArray());
        try
        {
            if (!document.RootElement.TryGetProperty("ruleUsage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<CssCoverageEntry> entries = new();
            foreach (JsonElement rule in usage.EnumerateArray())
            {
                entries.Add(new CssCoverageEntry(
                    rule.TryGetProperty("styleSheetId", out JsonElement styleSheetId)
                        ? styleSheetId.GetString() ?? string.Empty
                        : string.Empty,
                    rule.TryGetProperty("startOffset", out JsonElement start) ? start.GetInt32() : 0,
                    rule.TryGetProperty("endOffset", out JsonElement end) ? end.GetInt32() : 0,
                    rule.TryGetProperty("used", out JsonElement used) && used.GetBoolean()));
            }

            return entries;
        }
        finally
        {
            document.Dispose();
        }
    }
}
