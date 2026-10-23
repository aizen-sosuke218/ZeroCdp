using System.Diagnostics;
using System.Threading.Channels;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>Video recording options for a browser context.</summary>
public sealed class RecordVideoOptions
{
    /// <summary>Directory where finished WebM files are written.</summary>
    public string Dir { get; init; } = string.Empty;

    /// <summary>
    /// Output size. Null uses the context viewport, or 1280x720 when no viewport is configured.
    /// </summary>
    public ViewportSize? Size { get; init; }

    /// <summary>
    /// Optional path to an ffmpeg executable. Use this when shipping ffmpeg beside the application
    /// instead of relying on <c>PATH</c>. <c>ZEROCDP_FFMPEG_PATH</c> and <c>FFMPEG_PATH</c> are also
    /// honored.
    /// </summary>
    public string? FfmpegPath { get; init; }
}

/// <summary>
/// A video attached to a page. The file is finalized when the page closes; call
/// <see cref="PathAsync"/> to wait for it.
/// </summary>
public sealed class Video
{
    private readonly VideoRecorder _recorder;

    internal Video(VideoRecorder recorder) => _recorder = recorder;

    /// <summary>Path of the finished WebM file, waiting for the page to close.</summary>
    public ValueTask<string> PathAsync(CancellationToken cancellationToken = default)
        => _recorder.PathAsync(cancellationToken);

    /// <summary>Copies the finished video to <paramref name="path"/>.</summary>
    public async ValueTask SaveAsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string source = await PathAsync(cancellationToken).ConfigureAwait(false);
        File.Copy(source, path, overwrite: true);
    }

    /// <summary>Deletes the video file.</summary>
    public async ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        string path = await PathAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal ValueTask StopAsync() => _recorder.StopAsync();

    internal void StopInBackground() => _recorder.StopInBackground();
}

/// <summary>
/// Drives <c>Page.startScreencast</c> and pipes JPEG frames into ffmpeg. The screencast callback
/// only copies and enqueues frames; encoding happens on a background reader so the CDP event
/// thread is never blocked by disk or process I/O.
/// </summary>
internal sealed class VideoRecorder
{
    private const int FramesPerSecond = 25;

    private readonly ScreencastHub _hub;
    private readonly string _outputPath;
    private readonly string? _ffmpegPath;
    private readonly int _width;
    private readonly int _height;
    private readonly Channel<byte[]> _frames;
    private readonly TaskCompletionSource<string> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();

    private Process? _process;
    private Stream? _input;
    private ScreencastHub.Subscription? _subscription;
    private Task _writerTask = Task.CompletedTask;
    private Task _errorTask = Task.CompletedTask;
    private int _started;
    private int _stopped;

    internal VideoRecorder(
        ScreencastHub hub,
        string directory,
        ViewportSize size,
        string? ffmpegPath = null)
    {
        _hub = hub;
        _outputPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".webm");
        _ffmpegPath = ffmpegPath;
        _width = size.Width;
        _height = size.Height;
        _frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(90)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

    internal string OutputPath => _outputPath;

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        string? ffmpeg = FindFfmpeg(_ffmpegPath)
            ?? throw new InvalidOperationException(
                "Video recording requires ffmpeg on PATH, in ZEROCDP_FFMPEG_PATH, or through RecordVideo.FfmpegPath.");

        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        _process = StartFfmpeg(ffmpeg, _outputPath, _width, _height);
        _input = _process.StandardInput.BaseStream;
        _errorTask = DrainErrorsAsync(_process);
        _writerTask = Task.Run(WriteFramesAsync, CancellationToken.None);

        try
        {
            _subscription = await _hub
                .SubscribeAsync(_width, _height, OnFrame, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<string> PathAsync(CancellationToken cancellationToken)
        => new(_completed.Task.WaitAsync(cancellationToken));

    internal async ValueTask StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            await _completed.Task.ConfigureAwait(false);
            return;
        }

        ScreencastHub.Subscription? subscription = Interlocked.Exchange(ref _subscription, null);
        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        await StopCoreAsync().ConfigureAwait(false);
    }

    internal void StopInBackground() => _ = StopInBackgroundAsync();

    private async Task StopInBackgroundAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The target is already gone; nothing else can be finalized.
        }
    }

    internal static string? FindFfmpeg(string? preferredPath = null)
    {
        if (!string.IsNullOrEmpty(preferredPath) && File.Exists(preferredPath))
        {
            return preferredPath;
        }

        string? configured = Environment.GetEnvironmentVariable("ZEROCDP_FFMPEG_PATH");
        if (string.IsNullOrEmpty(configured))
        {
            configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        }

        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return configured;
        }

        string executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (directory.Length == 0)
                {
                    continue;
                }

                string candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (string candidate in new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static Process StartFfmpeg(string executable, string output, int width, int height)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libvpx-vp9");
        startInfo.ArgumentList.Add("-b:v");
        startInfo.ArgumentList.Add("1M");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(
            $"{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}x{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        startInfo.ArgumentList.Add(output);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start ffmpeg.");
    }

    private void OnFrame(ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        _frames.Writer.TryWrite(data.ToArray());
    }

    private async Task WriteFramesAsync()
    {
        try
        {
            await foreach (byte[] frame in _frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Stream? input = _input;
                if (input is null)
                {
                    break;
                }

                await input.WriteAsync(frame).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg exits when the target closes; StopAsync reports the file path.
        }
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try
        {
            _ = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async ValueTask StopCoreAsync()
    {
        _frames.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Encoding errors are surfaced by PathAsync's file existence check if they matter.
        }

        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            _completed.TrySetResult(_outputPath);
            return;
        }

        try
        {
            process.StandardInput.Close();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await _errorTask.ConfigureAwait(false);
            if (File.Exists(_outputPath))
            {
                _completed.TrySetResult(_outputPath);
            }
            else
            {
                _completed.TrySetException(
                    new InvalidOperationException("ffmpeg produced no video file."));
            }
        }
        catch (Exception ex)
        {
            _completed.TrySetException(ex);
        }
        finally
        {
            process.Dispose();
            _input = null;
            _completed.TrySetResult(_outputPath);
        }
    }
}
