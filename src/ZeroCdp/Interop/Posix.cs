using System.Runtime.InteropServices;

namespace ZeroCdp.Interop;

/// <summary>
/// The little bit of libc needed to spawn a browser with the DevTools pipe on fd 3 and fd 4.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Process"/> cannot pass extra file descriptors into the child, so
/// the pipe transport spawns through <c>posix_spawnp</c> instead. This is also what makes the
/// spawn allocation-light: no managed process wrapper is created on the launch path.
/// </remarks>
internal static unsafe partial class Posix
{
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
    public const int O_WRONLY = 1;
    public const int O_RDWR = 2;

    /// <summary>
    /// Opaque <c>posix_spawn_file_actions_t</c>. glibc's struct is 80 bytes, macOS's is 16, so a
    /// 512-byte, pointer-aligned blob is safe for both and for musl.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpawnFileActions
    {
        private fixed long _storage[64];

        public static int SizeOf => sizeof(SpawnFileActions);
    }

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    public static partial int CreatePipe(int* fds);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    public static partial int FileActionsInit(SpawnFileActions* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    public static partial int FileActionsAddDup2(SpawnFileActions* actions, int fd, int newFd);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    public static partial int FileActionsAddClose(SpawnFileActions* actions, int fd);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int FileActionsAddOpen(SpawnFileActions* actions, int fd, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    public static partial int FileActionsDestroy(SpawnFileActions* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawnp", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SpawnP(int* pid, string path, SpawnFileActions* actions, nint attributes, nint argv, nint envp);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    public static partial int WaitPid(int pid, int* status, int options);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    /// <summary>Kills and reaps a pid without blocking the caller.</summary>
    public static void Terminate(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        Kill(pid, SIGKILL);
        WaitPid(pid, null, 0);
    }
}
