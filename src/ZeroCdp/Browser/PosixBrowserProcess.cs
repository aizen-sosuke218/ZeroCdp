using System.Collections;
using System.Runtime.InteropServices;
using ZeroCdp.Interop;

namespace ZeroCdp.Browser;

/// <summary>
/// A browser started with <c>--remote-debugging-pipe</c>: the child reads CDP on fd 3 and writes
/// on fd 4, exactly as Chromium's pipe handler expects.
/// </summary>
/// <remarks>
/// Nothing listens on a socket, so there is no debugger port for another local process to find.
/// </remarks>
internal sealed unsafe class PosixBrowserProcess : IPipeBrowserProcess
{
    private const int ChildReadFd = 3;
    private const int ChildWriteFd = 4;

    private readonly int _pid;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stream _input;
    private readonly Stream _output;
    private int _disposed;

    private PosixBrowserProcess(int pid, Stream input, Stream output)
    {
        _pid = pid;
        _input = input;
        _output = output;

        Thread waiter = new(WaitForExitOnThread)
        {
            IsBackground = true,
            Name = "zerocdp-child-reaper",
        };
        waiter.Start();
    }

    /// <summary>Browser output (child fd 4) that CDP commands come back on.</summary>
    public Stream Input => _input;

    /// <summary>Browser input (child fd 3) that CDP commands are written to.</summary>
    public Stream Output => _output;

    public string TransportDescription => "pipe(fd3/fd4)";

    public Task Drain => Task.CompletedTask;

    public int Id => _pid;

    public bool HasExited => _exit.Task.IsCompleted;

    public int ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : -1;

    public static PosixBrowserProcess Start(string executable, IReadOnlyList<string> arguments, bool inheritStdio)
    {
        int* toChild = stackalloc int[2];
        int* fromChild = stackalloc int[2];

        if (Posix.CreatePipe(toChild) != 0)
        {
            throw new CdpException($"pipe() failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        if (Posix.CreatePipe(fromChild) != 0)
        {
            ThrowWithCleanup(Marshal.GetLastPInvokeErrorMessage(), toChild[0], toChild[1]);
        }

        Posix.SpawnFileActions actions = default;
        if (Posix.FileActionsInit(&actions) != 0)
        {
            ThrowWithCleanup(Marshal.GetLastPInvokeErrorMessage(), toChild[0], toChild[1], fromChild[0], fromChild[1]);
        }

        nint argv = 0;
        nint envp = 0;
        int pid = 0;
        try
        {
            // Child side: read commands on 3, write responses on 4.
            Check(Posix.FileActionsAddDup2(&actions, toChild[0], ChildReadFd), "adddup2 fd3");
            Check(Posix.FileActionsAddDup2(&actions, fromChild[1], ChildWriteFd), "adddup2 fd4");

            // Every original descriptor must go away in the child, or the pipe never reaches EOF.
            CloseIfNotTarget(&actions, toChild[0]);
            CloseIfNotTarget(&actions, toChild[1]);
            CloseIfNotTarget(&actions, fromChild[0]);
            CloseIfNotTarget(&actions, fromChild[1]);

            if (!inheritStdio)
            {
                // A library must not write the browser's chatter into its host's console. Passing
                // the exit code and CDP errors back is enough to debug a launch failure.
                _ = Posix.FileActionsAddOpen(&actions, 1, "/dev/null", Posix.O_WRONLY, 0);
                _ = Posix.FileActionsAddOpen(&actions, 2, "/dev/null", Posix.O_WRONLY, 0);
            }

            List<string> argvList = new(arguments.Count) { executable };
            argvList.AddRange(arguments);
            argv = AllocateStringArray(argvList);
            envp = AllocateEnvironment();

            int result = Posix.SpawnP(&pid, executable, &actions, 0, argv, envp);
            if (result != 0)
            {
                throw new CdpException($"posix_spawnp failed for '{executable}': {Marshal.GetPInvokeErrorMessage(result)}");
            }
        }
        catch
        {
            FreeStringArray(argv);
            FreeStringArray(envp);
            _ = Posix.FileActionsDestroy(&actions);
            _ = Posix.Close(toChild[0]);
            Posix.Close(toChild[1]);
            Posix.Close(fromChild[0]);
            Posix.Close(fromChild[1]);
            throw;
        }

        FreeStringArray(argv);
        FreeStringArray(envp);
        _ = Posix.FileActionsDestroy(&actions);

        // Parent keeps only its own ends of the two pipes.
        _ = Posix.Close(toChild[0]);
        _ = Posix.Close(fromChild[1]);

        Stream input = OpenFd(fromChild[0], FileAccess.Read);
        Stream output = OpenFd(toChild[1], FileAccess.Write);
        return new PosixBrowserProcess(pid, input, output);
    }

    private static void CloseIfNotTarget(Posix.SpawnFileActions* actions, int fd)
    {
        if (fd is ChildReadFd or ChildWriteFd)
        {
            return;
        }

        _ = Posix.FileActionsAddClose(actions, fd);
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new CdpException($"{what} failed: {Marshal.GetPInvokeErrorMessage(result)}");
        }
    }

    private static void ThrowWithCleanup(string message, params int[] fds)
    {
        foreach (int fd in fds)
        {
            _ = Posix.Close(fd);
        }

        throw new CdpException($"pipe() failed: {message}");
    }

    private static FileStream OpenFd(int fd, FileAccess access)
    {
        Microsoft.Win32.SafeHandles.SafeFileHandle handle = new((nint)fd, ownsHandle: true);
        // Unbuffered: a command has to leave the moment it is written.
        return new FileStream(handle, access, bufferSize: 0, isAsync: false);
    }

    private void WaitForExitOnThread()
    {
        int status = 0;
        int result = Posix.WaitPid(_pid, &status, 0);
        int exitCode = result == _pid ? (status >> 8) & 0xFF : -1;
        _exit.TrySetResult(exitCode);
    }

    public void Kill()
    {
        if (!HasExited)
        {
            Posix.Kill(_pid, Posix.SIGKILL);
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();

        try
        {
            _exit.Task.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _input.Dispose();
        _output.Dispose();
    }

    private static nint AllocateStringArray(List<string> values)
    {
        nint block = Marshal.AllocHGlobal(IntPtr.Size * (values.Count + 1));
        for (int i = 0; i < values.Count; i++)
        {
            Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
        }

        Marshal.WriteIntPtr(block, values.Count * IntPtr.Size, 0);
        return block;
    }

    private static nint AllocateEnvironment()
    {
        List<string> entries = new();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                entries.Add($"{key}={value}");
            }
        }

        return AllocateStringArray(entries);
    }

    private static void FreeStringArray(nint block)
    {
        if (block == 0)
        {
            return;
        }

        for (int i = 0; ; i++)
        {
            nint pointer = Marshal.ReadIntPtr(block, i * IntPtr.Size);
            if (pointer == 0)
            {
                break;
            }

            Marshal.FreeCoTaskMem(pointer);
        }

        Marshal.FreeHGlobal(block);
    }
}
