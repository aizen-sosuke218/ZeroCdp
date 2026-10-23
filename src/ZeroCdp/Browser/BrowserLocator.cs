using System.Runtime.InteropServices;

namespace ZeroCdp.Browser;

/// <summary>
/// Finds a Chromium-family executable that is already installed on the machine.
/// </summary>
/// <remarks>
/// ZeroCDP never downloads a browser: pulling a binary from the network at runtime is a supply
/// chain risk that an automation library should not take on the user's behalf.
/// </remarks>
public static class BrowserLocator
{
    /// <summary>Environment variable that overrides every other lookup.</summary>
    public const string OverrideVariable = "ZEROCDP_CHROME_PATH";

    private static readonly string[] LinuxCandidates =
    [
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        "/snap/bin/chromium",
        "/opt/google/chrome/chrome",
    ];

    private static readonly string[] MacCandidates =
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ];

    private static readonly string[] WindowsCandidates =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ];

    /// <summary>Returns the first usable browser path, or <see langword="null"/>.</summary>
    public static string? Find()
    {
        string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return overridden;
        }

        foreach (string candidate in GetCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath();
    }

    private static string[] GetCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCandidates;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacCandidates;
        }

        return LinuxCandidates;
    }

    private static string? FindOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        ReadOnlySpan<char> separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
        string[] names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["chrome.exe", "msedge.exe"]
            : ["google-chrome", "chromium", "chromium-browser"];

        foreach (Range directoryRange in path.AsSpan().Split(separator))
        {
            ReadOnlySpan<char> directory = path.AsSpan()[directoryRange];
            foreach (string name in names)
            {
                string full = Path.Combine(directory.ToString(), name);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }
}
