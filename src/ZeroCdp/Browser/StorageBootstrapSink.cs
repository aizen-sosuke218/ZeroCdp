using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>
/// Temporary event sink used while seeding local storage. It captures the paused navigation
/// request so it can be fulfilled locally, and the committed frame URL so the DOMStorage command
/// runs only after the frame has the target origin.
/// </summary>
internal sealed class StorageBootstrapSink : ICdpEventSink
{
    private readonly TaskCompletionSource<string> _requestId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<string> _navigationUrl =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> RequestId => _requestId.Task;

    public Task<string> NavigationUrl => _navigationUrl.Task;

    public void OnEvent(
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<byte> method,
        ReadOnlySpan<byte> paramsUtf8)
    {
        if (method.SequenceEqual("Fetch.requestPaused"u8))
        {
            JsonCursor cursor = new(paramsUtf8);
            if (cursor.TryEnterObject()
                && cursor.TrySeekProperty("requestId"u8)
                && cursor.TryReadString(out ReadOnlySpan<byte> rawId, out _))
            {
                _requestId.TrySetResult(Encoding.UTF8.GetString(rawId));
            }
        }
        else if (method.SequenceEqual("Page.frameNavigated"u8))
        {
            JsonCursor cursor = new(paramsUtf8);
            if (cursor.TryEnterObject()
                && cursor.TrySeekProperty("frame"u8)
                && cursor.TryEnterObject()
                && cursor.TrySeekProperty("url"u8)
                && cursor.TryReadString(out ReadOnlySpan<byte> rawUrl, out _))
            {
                _navigationUrl.TrySetResult(Encoding.UTF8.GetString(rawUrl));
            }
        }
    }
}
