namespace ZeroCdp.Browser;

/// <summary>Creates and removes the private browsing profile a launch owns.</summary>
internal static class ProfileDirectory
{
    /// <summary>
    /// Creates a directory only the current user can read. The profile holds cookies, local
    /// storage and session state, so world-readable permissions would be a real leak.
    /// </summary>
    public static string Create()
    {
        string path = Path.Combine(Path.GetTempPath(), "zerocdp-" + Guid.NewGuid().ToString("N"));

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, ownerOnly);
        }

        return path;
    }

    /// <summary>
    /// Removes the profile, retrying briefly: Chromium's helper processes can still be unlinking
    /// files while the main process exits, and a recursive delete that loses that race leaves an
    /// empty directory behind.
    /// </summary>
    public static async ValueTask DeleteAsync(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                // Retry below.
            }
            catch (UnauthorizedAccessException)
            {
                // Retry below.
            }

            await Task.Delay(75).ConfigureAwait(false);
        }
    }
}
