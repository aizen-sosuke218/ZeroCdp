using System.Diagnostics;
using ZeroCdp.Dom;
using ZeroCdp.Protocol;
using ZeroCdp.Selectors;

namespace ZeroCdp.Browser;

public sealed partial class Locator
{
    /// <summary>Captures the first match as an image.</summary>
    public ValueTask<byte[]> ScreenshotBytesAsync(
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => ScreenshotBytesCoreAsync(options, options?.Timeout, cancellationToken);

    /// <summary>Playwright-style locator screenshot bytes overload.</summary>
    public ValueTask<byte[]> ScreenshotBytesAsync(
        LocatorScreenshotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ScreenshotBytesCoreAsync(ToNative(options), options.Timeout, cancellationToken);
    }

    private async ValueTask<byte[]> ScreenshotBytesCoreAsync(
        ScreenshotOptions? options,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        long nodeId = await WaitForActionAsync(
                requireVisible: true,
                requireEnabled: false,
                requireEditable: false,
                requirement: "visible",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        return await _page.ScreenshotNodeBytesAsync(nodeId, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures the first match and writes it to <paramref name="path"/>.</summary>
    public async ValueTask ScreenshotAsync(
        string path,
        ScreenshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Playwright-style locator screenshot returning the image bytes.</summary>
    public async ValueTask<byte[]> ScreenshotAsync(
        LocatorScreenshotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] data = await ScreenshotBytesAsync(options, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(options.Path))
        {
            string path = options.Path;
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
        }

        return data;
    }

    private static ScreenshotOptions ToNative(LocatorScreenshotOptions options) => new()
    {
        Format = options.Type.ToScreenshotFormat(),
        Quality = options.Quality,
        OmitBackground = options.OmitBackground,
        Timeout = options.Timeout,
        Mask = options.Mask,
        MaskColor = options.MaskColor,
        Animations = options.Animations,
        Caret = options.Caret,
        Scale = options.Scale,
        Style = options.Style,
    };

    /// <summary>Sets files on the first <c>input type=file</c>. An empty list clears the selection.</summary>
    public async ValueTask SetInputFilesAsync(
        params string[] files)        => await SetInputFilesAsync(files, CancellationToken.None).ConfigureAwait(false);

    public ValueTask SetInputFilesAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
        => SetInputFilesCoreAsync(files, timeout: null, cancellationToken);

    /// <summary>Playwright-style set-input-files overload using an options object.</summary>
    public ValueTask SetInputFilesAsync(
        IReadOnlyList<string> files,
        LocatorSetInputFilesOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SetInputFilesCoreAsync(files, options.Timeout, cancellationToken);
    }

    private async ValueTask SetInputFilesCoreAsync(
        IReadOnlyList<string> files,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        string[] absolutePaths = new string[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            string path = Path.GetFullPath(files[i]);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The upload file does not exist.", path);
            }

            absolutePaths[i] = path;
        }

        long nodeId = await WaitForActionAsync(
                requireVisible: false,
                requireEnabled: true,
                requireEditable: false,
                requirement: "attached and enabled",
                cancellationToken,
                timeout)
            .ConfigureAwait(false);

        DomNode? info = await _page.GetNodeInfoAsync(nodeId, cancellationToken).ConfigureAwait(false);
        string? type = info?.GetAttribute("type");
        if (info is null
            || info.LocalName != "input"
            || type is null
            || !type.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            throw new CdpException("SetInputFiles requires an input element of type 'file'.");
        }

        if (absolutePaths.Length == 0)
        {
            await _page.ClearFileInputAsync(nodeId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _page.SetFileInputFilesAsync(nodeId, absolutePaths, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Focuses the first match without dispatching a mouse click.</summary>
}
