using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public class BrowserLaunchTests
{
    [Fact]
    public async Task Launches_a_browser_and_reads_its_version()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent: no Chromium-family browser installed.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            StartupTimeout = TimeSpan.FromSeconds(30),
        });

        BrowserVersion version = await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8);

        string product = Encoding.UTF8.GetString(version.Product.Span);
        Assert.Contains("/", product, StringComparison.Ordinal);
        Assert.False(version.ProtocolVersion.IsEmpty);
        Assert.False(version.UserAgent.IsEmpty);

        // The default must be the pipe on every platform: no debugger socket is opened at all.
        Assert.Contains("pipe", browser.Connection.TransportDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connects_over_loopback_when_the_pipe_is_not_used()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Transport = CdpTransportKind.Port,
            StartupTimeout = TimeSpan.FromSeconds(30),
        });

        BrowserVersion version = await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8);

        Assert.False(version.Product.IsEmpty);
        Assert.Contains("websocket(127.0.0.1", browser.Connection.TransportDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creates_a_page_target_through_a_command_with_params()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
        });

        TargetCreated created = await browser.Connection
            .SendAsync<TargetCreated, CreateTargetParams>("Target.createTarget"u8, new CreateTargetParams("about:blank"));

        Assert.False(string.IsNullOrEmpty(created.TargetId));
    }

    [Fact]
    public async Task Runs_many_commands_in_sequence_without_leaking_slots()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
        });

        for (int i = 0; i < 200; i++)
        {
            BrowserVersion version = await browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8);
            Assert.False(version.Product.IsEmpty);
        }

        Assert.Equal(0, browser.Connection.PendingCount);
    }

    [Fact]
    public async Task Runs_commands_concurrently_without_corrupting_the_pending_table()
    {
        if (BrowserLocator.Find() is null)
        {
            return; // Environment dependent.
        }

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
        });

        Task<BrowserVersion>[] commands = new Task<BrowserVersion>[200];
        for (int i = 0; i < commands.Length; i++)
        {
            commands[i] = browser.Connection.SendAsync<BrowserVersion>("Browser.getVersion"u8).AsTask();
        }

        BrowserVersion[] results = await Task.WhenAll(commands);

        Assert.All(results, version => Assert.False(version.Product.IsEmpty));
        Assert.Equal(0, browser.Connection.PendingCount);
    }
}
