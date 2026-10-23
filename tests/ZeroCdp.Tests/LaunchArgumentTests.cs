using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public class LaunchArgumentTests
{
    private const string Profile = "/tmp/zerocdp-profile";

    [Fact]
    public void Raw_profile_leaves_out_automation_overrides()
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Stealth = StealthProfile.Raw, Headless = true },
            Profile,
            usePipe: true).ToArray();

        Assert.DoesNotContain("--disable-blink-features=AutomationControlled", args);
        Assert.Contains("--disable-gpu", args);
    }

    [Fact]
    public void Minimal_profile_disables_automation_controlled()
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Stealth = StealthProfile.Minimal, Headless = true },
            Profile,
            usePipe: true).ToArray();

        Assert.Contains("--disable-blink-features=AutomationControlled", args);
        Assert.Contains("--disable-gpu", args);
    }

    [Fact]
    public void Default_profile_drops_the_headless_gpu_fingerprint()
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Stealth = StealthProfile.Default, Headless = true },
            Profile,
            usePipe: true).ToArray();

        Assert.Contains("--disable-blink-features=AutomationControlled", args);
        Assert.DoesNotContain("--disable-gpu", args);
        Assert.Contains("--headless=new", args);
    }

    [Theory]
    [InlineData(StealthProfile.Raw)]
    [InlineData(StealthProfile.Minimal)]
    [InlineData(StealthProfile.Default)]
    public void No_profile_ever_adds_detectable_playwright_flags(StealthProfile profile)
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Stealth = profile, Headless = false },
            Profile,
            usePipe: true).ToArray();

        Assert.DoesNotContain("--enable-automation", args);
        Assert.DoesNotContain("--disable-component-update", args);
        Assert.DoesNotContain("--disable-default-apps", args);
        Assert.DoesNotContain("--disable-extensions", args);
        Assert.DoesNotContain("--disable-popup-blocking", args);
        Assert.DoesNotContain("--remote-allow-origins=*", args);
        Assert.DoesNotContain("--no-sandbox", args);
    }

    [Fact]
    public void Pipe_mode_never_opens_a_debugger_port()
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Headless = true },
            Profile,
            usePipe: true).ToArray();

        Assert.Contains("--remote-debugging-pipe", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
    }

    [Fact]
    public void Port_mode_stays_on_loopback()
    {
        string[] args = LaunchArguments.Build(
            new LaunchOptions { Headless = true },
            Profile,
            usePipe: false).ToArray();

        Assert.Contains("--remote-debugging-port=0", args);
        Assert.Contains("--remote-debugging-address=127.0.0.1", args);
        Assert.DoesNotContain("--remote-allow-origins=*", args);
    }
}
