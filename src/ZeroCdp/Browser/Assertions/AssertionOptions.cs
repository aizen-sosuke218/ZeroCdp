using System.Text.RegularExpressions;

namespace ZeroCdp.Browser;

/// <summary>Thrown when a web-first assertion does not become true before its timeout.</summary>
public sealed class CdpAssertionException : CdpException
{
    public CdpAssertionException(string message)
        : base(message)
    {
    }

    public CdpAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Common options for web-first assertions.</summary>
public class ExpectOptions
{
    /// <summary>Maximum time to keep polling. Defaults to five seconds.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Options for text, attribute and value assertions.</summary>
public sealed class LocatorAssertionOptions : ExpectOptions
{
    /// <summary>Match the complete value instead of a substring. Text assertions are exact by default.</summary>
    public bool Exact { get; init; }
}

/// <summary>Options for <see cref="LocatorAssertions.ToHaveScreenshotAsync"/>.</summary>
public sealed class LocatorScreenshotAssertionOptions : ExpectOptions
{
    /// <summary>Directory containing baselines. Defaults to <c>__snapshots__</c>.</summary>
    public string Directory { get; init; } = "__snapshots__";

    /// <summary>Replace the baseline instead of comparing against it.</summary>
    public bool UpdateSnapshots { get; init; }

    /// <summary>Freeze CSS animations while capturing.</summary>
    public ScreenshotAnimations Animations { get; init; } = ScreenshotAnimations.Disabled;

    public ScreenshotCaret Caret { get; init; } = ScreenshotCaret.Hide;

    public ScreenshotScale Scale { get; init; } = ScreenshotScale.Css;

    public IReadOnlyList<Locator>? Mask { get; init; }

    public string? MaskColor { get; init; }

    public string? Style { get; init; }
}

/// <summary>Shared helpers for web-first assertions.</summary>
internal static class AssertionHelpers
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    internal static async ValueTask RetryAsync(
        string description,
        Func<CancellationToken, ValueTask<bool>> predicate,
        TimeSpan? timeout,
        bool expected,
        CancellationToken cancellationToken)
    {
        TimeSpan effective = timeout ?? DefaultTimeout;
        if (effective <= TimeSpan.Zero)
        {
            effective = DefaultTimeout;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool last = false;
        Exception? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                last = await predicate(cancellationToken).ConfigureAwait(false);
                lastError = null;
            }
            catch (Exception ex) when (ex is CdpException or ElementNotFoundException or ObjectDisposedException)
            {
                last = false;
                lastError = ex;
            }

            if (last == expected)
            {
                return;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= effective)
            {
                string suffix = lastError is null ? string.Empty : " Last error: " + lastError.Message;
                throw new CdpAssertionException(
                    $"Expected {description} to {(expected ? "be true" : "be false")} within {effective.TotalMilliseconds:0} ms, but it was {(last ? "true" : "false")}.{suffix}",
                    lastError ?? new TimeoutException());
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async ValueTask RetryValueAsync<T>(
        string description,
        Func<CancellationToken, ValueTask<T>> read,
        Func<T, bool> predicate,
        TimeSpan? timeout,
        bool expected,
        CancellationToken cancellationToken,
        Func<T, string>? format = null)
    {
        T? lastValue = default;
        bool hasValue = false;
        Exception? lastError = null;

        await RetryAsync(
                description,
                async token =>
                {
                    try
                    {
                        T value = await read(token).ConfigureAwait(false);
                        lastValue = value;
                        hasValue = true;
                        lastError = null;
                        return predicate(value);
                    }
                    catch (Exception ex) when (ex is CdpException or ElementNotFoundException or ObjectDisposedException)
                    {
                        hasValue = false;
                        lastError = ex;
                        return false;
                    }
                },
                timeout,
                expected,
                cancellationToken)
            .ConfigureAwait(false);

        _ = lastValue;
        _ = hasValue;
        _ = lastError;
        _ = format;
    }

    internal static bool Matches(string? actual, string expected, bool exact)
    {
        actual ??= string.Empty;
        return exact
            ? string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal)
            : actual.Contains(expected, StringComparison.Ordinal);
    }

    internal static bool Matches(string? actual, Regex expected)
        => expected.IsMatch(actual ?? string.Empty);

    internal static string Normalize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;
        bool pendingSpace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer[length++] = ' ';
                pendingSpace = false;
            }

            buffer[length++] = c;
        }

        return new string(buffer[..length]);
    }

    internal static string FormatValue(object? value) => value switch
    {
        null => "null",
        string text => '"' + text + '"',
        _ => value.ToString() ?? "null",
    };
}
