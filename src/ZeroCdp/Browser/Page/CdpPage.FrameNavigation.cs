using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    internal async ValueTask<GotoResult> GotoFrameAsync(
        Frame frame,
        string url,
        WaitUntil waitUntil,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        url = ResolveNavigationUrl(url);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        if (frame.IsDetached)
        {
            throw new CdpException($"Frame '{frame.FrameId}' is detached.");
        }

        if (string.Equals(frame.FrameId, _mainFrameId, StringComparison.Ordinal))
        {
            return await GotoAsync(url, waitUntil, timeout, cancellationToken).ConfigureAwait(false);
        }

        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan effective = timeout ?? DefaultNavigationTimeout;
        FrameContext context = GetFrameContext(frameId);
        string previousUrl = frame.Url;

        NavigateResult navigation = await context.Session
            .SendAsync<NavigateResult, NavigateParams>(
                "Page.navigate"u8,
                new NavigateParams(url, context.Id == 0 ? frameId : null),
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(navigation.ErrorText))
        {
            throw new CdpNavigationException(url, navigation.ErrorText);
        }

        await WaitForFrameNavigationAsync(frame, previousUrl, url, waitUntil, effective, cancellationToken)
            .ConfigureAwait(false);

        return new GotoResult(
            navigation.FrameId ?? frameId,
            navigation.LoaderId,
            frame.Url.Length != 0 ? frame.Url : url);
    }

    internal async ValueTask SetFrameContentAsync(
        Frame frame,
        string html,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(html);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        string frameId = await frame.ResolveFrameIdAsync(cancellationToken).ConfigureAwait(false);
        FrameContext context = GetFrameContext(frameId);

        await context.Session
            .SendAsync<Unit, SetDocumentContentParams>(
                "Page.setDocumentContent"u8,
                new SetDocumentContentParams(frameId, html),
                cancellationToken)
            .ConfigureAwait(false);

        InvalidateWorlds();
        if (context.Id != 0)
        {
            context.World.Invalidate();
        }
    }

    private static async ValueTask WaitForFrameNavigationAsync(
        Frame frame,
        string previousUrl,
        string requestedUrl,
        WaitUntil waitUntil,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            string current = frame.Url;
            if (!string.Equals(current, previousUrl, StringComparison.Ordinal)
                || (!string.Equals(requestedUrl, previousUrl, StringComparison.Ordinal)
                    && current.Equals(requestedUrl, StringComparison.Ordinal)))
            {
                break;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Frame '{frame.FrameId}' did not commit navigation to '{requestedUrl}' " +
                    $"within {timeout.TotalSeconds:0.##}s.");
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        if (waitUntil == WaitUntil.Commit)
        {
            return;
        }

        TimeSpan remaining = TimeSpan.FromMilliseconds(
            Math.Max(0, deadline - Environment.TickCount64));
        await frame
            .WaitForLoadStateAsync(
                waitUntil == WaitUntil.NetworkIdle ? WaitUntil.Load : waitUntil,
                remaining,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
