using ZeroCdp.Json;

namespace ZeroCdp.Browser;

/// <summary>
/// A file chooser intercepted before Chromium shows the native dialog. Call
/// <see cref="AcceptAsync(IReadOnlyList{string}, CancellationToken)"/> with file paths, or
/// <see cref="CancelAsync"/> to leave the input empty.
/// </summary>
public sealed class FileChooser
{
    private readonly CdpPage _page;
    private int _handled;

    internal FileChooser(CdpPage page, string frameId, long backendNodeId, bool multiple)
    {
        _page = page;
        FrameId = frameId;
        BackendNodeId = backendNodeId;
        IsMultiple = multiple;
    }

    public CdpPage Page => _page;

    public string FrameId { get; }

    public bool IsMultiple { get; }

    internal long BackendNodeId { get; }

    /// <summary>Selects exactly one file for a single-file input.</summary>
    public ValueTask AcceptAsync(string file, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        return AcceptAsync(new[] { file }, cancellationToken);
    }

    /// <summary>
    /// Selects files for the intercepted input. Passing an empty list is equivalent to
    /// <see cref="CancelAsync"/> and clears the input.
    /// </summary>
    public async ValueTask AcceptAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        Interlocked.Exchange(ref _handled, 1);

        foreach (string file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("The file chooser path does not exist.", file);
            }
        }

        await _page.SetFileInputFilesByBackendNodeAsync(BackendNodeId, files, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selects in-memory files for the intercepted input.</summary>
    public async ValueTask SetFilesAsync(
        IReadOnlyList<FilePayload> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        Interlocked.Exchange(ref _handled, 1);
        await _page.SetFileInputPayloadsAsync(FrameId, BackendNodeId, files, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Cancels the chooser and clears any selected files.</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => AcceptAsync(Array.Empty<string>(), cancellationToken);

    internal bool IsHandled => Volatile.Read(ref _handled) != 0;
}

internal readonly struct FileChooserInfo
{
    public string FrameId { get; init; }

    public long BackendNodeId { get; init; }

    public bool IsMultiple { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out FileChooserInfo value)
    {
        value = default;
        string frameId = string.Empty;
        string mode = string.Empty;
        long backendNodeId = 0;

        JsonCursor cursor = new(utf8);
        if (!cursor.TryEnterObject())
        {
            return false;
        }

        while (cursor.TryNextProperty(out ReadOnlySpan<byte> name))
        {
            if (name.SequenceEqual("frameId"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawFrame, out _))
            {
                frameId = System.Text.Encoding.UTF8.GetString(rawFrame);
            }
            else if (name.SequenceEqual("mode"u8) && cursor.TryReadString(out ReadOnlySpan<byte> rawMode, out _))
            {
                mode = System.Text.Encoding.UTF8.GetString(rawMode);
            }
            else if (name.SequenceEqual("backendNodeId"u8))
            {
                cursor.TryGetInt64(out backendNodeId);
            }
            else if (!cursor.SkipValue())
            {
                return false;
            }
        }

        if (backendNodeId <= 0)
        {
            return false;
        }

        value = new FileChooserInfo
        {
            FrameId = frameId,
            BackendNodeId = backendNodeId,
            IsMultiple = mode.Equals("selectMultiple", StringComparison.Ordinal),
        };
        return true;
    }
}
