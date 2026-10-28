using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>Lifecycle of a browser download.</summary>
public enum DownloadState
{
    InProgress = 0,
    Completed = 1,
    Canceled = 2,
}

/// <summary>
/// One download started by a page. The browser writes it under its GUID in
/// <see cref="Browser.DownloadsDirectory"/>; call <see cref="WaitForCompletionAsync"/> before
/// reading <see cref="Path"/> when the file must be complete.
/// </summary>
public sealed class Download
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DownloadState _state;
    private long _receivedBytes;
    private long _totalBytes;

    internal Download(
        CdpPage page,
        string guid,
        string url,
        string suggestedFilename,
        string path)
    {
        Page = page;
        DownloadId = guid;
        Url = url;
        SuggestedFilename = suggestedFilename;
        Path = path;
    }

    public CdpPage Page { get; }

    /// <summary>CDP download id; unique for the browser process.</summary>
    public string DownloadId { get; }

    public string Url { get; }

    public string SuggestedFilename { get; }

    /// <summary>
    /// Full path chosen by the browser. With Chromium's <c>allowAndName</c> behavior the final
    /// segment is <see cref="DownloadId"/>. The file may still be growing until completion.
    /// </summary>
    public string Path { get; }

    public DownloadState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsCompleted => State == DownloadState.Completed;

    public bool IsCanceled => State == DownloadState.Canceled;

    public long ReceivedBytes
    {
        get
        {
            lock (_gate)
            {
                return _receivedBytes;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>Reason the download did not complete, or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>Waits until the browser reports completion or cancellation.</summary>
    public async ValueTask WaitForCompletionAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan effective = timeout ?? Page.DefaultTimeout;
        try
        {
            await _completion.Task.WaitAsync(effective, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CdpTimeoutException(
                $"Download '{SuggestedFilename}' did not finish within {effective.TotalSeconds:0.###}s.");
        }

        if (IsCanceled)
        {
            throw new CdpException($"Download '{SuggestedFilename}' was canceled.");
        }
    }

    /// <summary>Waits for completion and returns the browser-chosen file path.</summary>
    public async ValueTask<string> PathAsync(CancellationToken cancellationToken = default)
    {
        await WaitForCompletionAsync(timeout: null, cancellationToken).ConfigureAwait(false);
        return Path;
    }

    /// <summary>
    /// Waits for completion and opens the downloaded file for reading. The caller owns the
    /// returned stream.
    /// </summary>
    public async ValueTask<Stream> CreateReadStreamAsync(CancellationToken cancellationToken = default)
    {
        await WaitForCompletionAsync(timeout: null, cancellationToken).ConfigureAwait(false);
        return new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
    }

    /// <summary>Playwright-style async accessor for <see cref="Url"/>.</summary>
    public ValueTask<string> UrlAsync() => ValueTask.FromResult(Url);

    /// <summary>Playwright-style async accessor for <see cref="SuggestedFilename"/>.</summary>
    public ValueTask<string> SuggestedFilenameAsync() => ValueTask.FromResult(SuggestedFilename);

    /// <summary>Asks the browser to cancel an in-flight download.</summary>
    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        if (State != DownloadState.InProgress)
        {
            return;
        }

        await Page.Context.Browser.Connection
            .SendAsync<Protocol.Unit, Protocol.CancelDownloadParams>(
                "Browser.cancelDownload"u8,
                new Protocol.CancelDownloadParams(DownloadId, Page.Context.BrowserContextId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Playwright-style async accessor for <see cref="Failure"/>.</summary>
    public ValueTask<string?> FailureAsync() => ValueTask.FromResult(Failure);

    /// <summary>Waits for completion and copies the file to <paramref name="destination"/>.</summary>
    public async ValueTask SaveAsAsync(
        string destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        await WaitForCompletionAsync(timeout: null, cancellationToken).ConfigureAwait(false);

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(Path, destination, overwrite);
    }

    /// <summary>Deletes the downloaded file when it exists.</summary>
    public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return ValueTask.CompletedTask;
    }

    internal void ReportProgress(long receivedBytes, long totalBytes, string state)
    {
        bool complete = false;
        lock (_gate)
        {
            _receivedBytes = receivedBytes;
            _totalBytes = totalBytes;

            if (state.Equals("completed", StringComparison.Ordinal))
            {
                _state = DownloadState.Completed;
                complete = true;
            }
            else if (state.Equals("canceled", StringComparison.Ordinal))
            {
                _state = DownloadState.Canceled;
                Failure = "canceled";
                complete = true;
            }
        }

        if (complete)
        {
            _completion.TrySetResult(true);
        }
    }
}

internal readonly struct DownloadBeginInfo
{
    public string Guid { get; init; }

    public string Url { get; init; }

    public string SuggestedFilename { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out DownloadBeginInfo value)
    {
        value = default;
        string guid = string.Empty;
        string url = string.Empty;
        string suggested = string.Empty;

        JsonCursor cursor = new(utf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("guid"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawGuid, out _))
            {
                guid = System.Text.Encoding.UTF8.GetString(rawGuid);
            }
            else if (name.SequenceEqual("url"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawUrl, out _))
            {
                url = System.Text.Encoding.UTF8.GetString(rawUrl);
            }
            else if (name.SequenceEqual("suggestedFilename"u8)
                && cursor.TryReadString(out ReadOnlySpan<byte> rawName, out _))
            {
                suggested = System.Text.Encoding.UTF8.GetString(rawName);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (guid.Length == 0)
        {
            return false;
        }

        value = new DownloadBeginInfo
        {
            Guid = guid,
            Url = url,
            SuggestedFilename = suggested,
        };
        return true;
    }
}

internal readonly struct DownloadProgressInfo
{
    public string Guid { get; init; }

    public long ReceivedBytes { get; init; }

    public long TotalBytes { get; init; }

    public string State { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out DownloadProgressInfo value)
    {
        value = default;
        string guid = string.Empty;
        string state = string.Empty;
        long received = 0;
        long total = 0;

        JsonCursor cursor = new(utf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("guid"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawGuid, out _))
            {
                guid = System.Text.Encoding.UTF8.GetString(rawGuid);
            }
            else if (name.SequenceEqual("state"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawState, out _))
            {
                state = System.Text.Encoding.UTF8.GetString(rawState);
            }
            else if (name.SequenceEqual("receivedBytes"u8))
            {
                cursor.TryGetInt64(out received);
            }
            else if (name.SequenceEqual("totalBytes"u8))
            {
                cursor.TryGetInt64(out total);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (guid.Length == 0 || state.Length == 0)
        {
            return false;
        }

        value = new DownloadProgressInfo
        {
            Guid = guid,
            ReceivedBytes = received,
            TotalBytes = total,
            State = state,
        };
        return true;
    }
}
