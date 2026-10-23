namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    internal async ValueTask StartVideoAsync(
        BrowserContextOptions options,
        CancellationToken cancellationToken)
    {
        if (options.RecordVideo is not { } recordVideo)
        {
            return;
        }

        ViewportSize size = recordVideo.Size
            ?? options.Viewport
            ?? ViewportSize.Default;
        VideoRecorder recorder = new(Screencast, recordVideo.Dir, size, recordVideo.FfmpegPath);
        _video = new Video(recorder);
        await recorder.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask StopVideoAsync()
    {
        Video? video = _video;
        if (video is null)
        {
            return;
        }

        try
        {
            await video.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Closing the page must not fail because the encoder or target disappeared.
        }
    }

    internal async ValueTask StopScreencastAsync()
    {
        ScreencastHub? screencast = _screencast;
        if (screencast is null)
        {
            return;
        }

        try
        {
            await screencast.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The page is going away; its screencast has no useful consumers left.
        }
    }
}
