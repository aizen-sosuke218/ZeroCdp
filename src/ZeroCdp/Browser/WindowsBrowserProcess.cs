using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace ZeroCdp.Browser;

/// <summary>
/// A Windows browser started with <c>--remote-debugging-pipe</c> and two inherited anonymous-pipe
/// handles passed through <c>--remote-debugging-io-pipes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Chromium does not use the CRT fds 3/4 on Windows. It parses the switch as two decimal handle
/// values, verifies that each handle is a pipe, and adopts them with <c>_open_osfhandle</c>. The
/// handles keep the same value in the child because they are inherited.
/// </para>
/// <para>
/// <see cref="System.Diagnostics.Process.Start(ProcessStartInfo)"/> calls <c>CreateProcess</c> with
/// <c>bInheritHandles = TRUE</c>, and <see cref="AnonymousPipeServerStream"/> created with
/// <see cref="HandleInheritability.Inheritable"/> marks the client ends inheritable. This is the
/// same process/PipeStart shape PuppeteerSharp uses for its Windows pipe transport.
/// </para>
/// <para>
/// Once the child has inherited the client handles, the parent closes its local copies with
/// <see cref="AnonymousPipeServerStream.DisposeLocalCopyOfClientHandle"/>. That is what lets the
/// browser observe EOF when the parent closes the connection instead of waiting on a handle held
/// open by the launcher.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserProcess : IPipeBrowserProcess
{
    private readonly Process _process;
    private readonly AnonymousPipeServerStream _input;
    private readonly AnonymousPipeServerStream _output;
    private readonly Task _drain;
    private int _disposed;

    private WindowsBrowserProcess(
        Process process,
        AnonymousPipeServerStream input,
        AnonymousPipeServerStream output,
        Task drain)
    {
        _process = process;
        _input = input;
        _output = output;
        _drain = drain;
    }

    /// <summary>Browser output (the write handle given to the child) that CDP responses arrive on.</summary>
    public Stream Input => _input;

    /// <summary>Browser input (the read handle given to the child) that CDP commands are sent to.</summary>
    public Stream Output => _output;

    public string TransportDescription => "pipe(windows-handles)";

    public Task Drain => _drain;

    public int Id
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            try
            {
                return _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Starts the browser and gives it inherited pipe handles. The caller owns the returned
    /// process and must dispose it.
    /// </summary>
    public static WindowsBrowserProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        bool inheritStdio)
    {
        AnonymousPipeServerStream? fromChild = null;
        AnonymousPipeServerStream? toChild = null;
        Process? process = null;

        try
        {
            // The child writes responses into the server-read end; it reads commands from the
            // server-write end. Both client handles are inheritable for exactly this launch.
            fromChild = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            toChild = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

            string readHandle = toChild.GetClientHandleAsString();
            string writeHandle = fromChild.GetClientHandleAsString();

            ProcessStartInfo startInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = !inheritStdio,
                RedirectStandardOutput = !inheritStdio,
            };

            // Chromium parses this as two unsigned decimal HANDLE values joined by a comma:
            // first the read end (commands), then the write end (responses). The child has the
            // same handle values because they were inherited. Keep the switch ahead of the URL
            // argument so the command line is unambiguous even for a future parser change.
            startInfo.ArgumentList.Add($"--remote-debugging-io-pipes={readHandle},{writeHandle}");

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo)
                ?? throw new CdpException("The browser process could not be started.");

            // The child now owns its copies. Keep only the server ends in this process so closing
            // the connection produces EOF in the browser rather than a leaked handle doing so.
            toChild.DisposeLocalCopyOfClientHandle();
            fromChild.DisposeLocalCopyOfClientHandle();

            Task drain = inheritStdio
                ? Task.CompletedTask
                : ProcessDrain.DrainAsync(process);

            return new WindowsBrowserProcess(process, fromChild, toChild, drain);
        }
        catch
        {
            if (process is not null)
            {
                Kill(process);
                process.Dispose();
            }

            fromChild?.Dispose();
            toChild?.Dispose();
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    public void Kill() => Kill(_process);

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _process.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
