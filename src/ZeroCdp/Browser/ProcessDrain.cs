using System.Buffers;
using System.Diagnostics;

namespace ZeroCdp.Browser;

/// <summary>Drains a browser's redirected stdout/stderr without retaining the output.</summary>
internal static class ProcessDrain
{
    /// <summary>
    /// Consumes both redirected streams for as long as the process lives.
    /// </summary>
    /// <remarks>
    /// This must never stop early: if nobody drains the pipes the OS buffer fills up and the browser
    /// blocks. The content is discarded, with a fixed-size buffer, so a chatty browser cannot grow
    /// our memory.
    /// </remarks>
    public static Task DrainAsync(Process process)
        => Task.WhenAll(DrainStreamAsync(process.StandardError), DrainStreamAsync(process.StandardOutput));

    private static async Task DrainStreamAsync(StreamReader reader)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
