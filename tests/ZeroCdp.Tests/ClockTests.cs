using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public sealed class ClockTests
{
    [Fact]
    public async Task Clock_installs_across_navigation_and_controls_timers_and_date()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        await using LocalHttpServer server = new()
        {
            Handler = _ => new TestHttpResponse
            {
                Body =
                    """
                    <!doctype html>
                    <html>
                    <head><title>clock</title></head>
                    <body>
                      <div id="now"></div>
                      <script>
                        window.__clockEvents = [];
                        function render() {
                          document.getElementById('now').textContent = String(Date.now());
                        }
                        render();
                        setTimeout(() => {
                          window.__clockEvents.push('timeout');
                          render();
                        }, 1000);
                        setInterval(() => {
                          window.__clockEvents.push('interval');
                          render();
                        }, 1000);
                      </script>
                    </body>
                    </html>
                    """,
            },
        };

        DateTimeOffset start = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        DateTimeOffset pausedAt = start.AddSeconds(10);
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();

        await page.Clock.InstallAsync(new ClockInstallOptions { Time = start });
        await page.Clock.PauseAtAsync(pausedAt);
        await page.GotoAsync(server.Url("/clock"));

        Assert.Equal(pausedAt.ToUnixTimeMilliseconds(), await DateNowAsync(page));

        await page.Clock.RunForAsync(3000);
        Assert.Equal(
            pausedAt.AddSeconds(3).ToUnixTimeMilliseconds(),
            await DateNowAsync(page));

        EvaluationResult events = await page.EvaluateAsync(
            "JSON.stringify(window.__clockEvents)",
            isolatedContext: false);
        string serialized = events.Value.StringValue ?? string.Empty;
        Assert.Equal(1, Count(serialized, "timeout"));
        Assert.Equal(3, Count(serialized, "interval"));

        await page.Clock.FastForwardAsync(10000);
        Assert.Equal(
            pausedAt.AddSeconds(13).ToUnixTimeMilliseconds(),
            await DateNowAsync(page));
        events = await page.EvaluateAsync(
            "JSON.stringify(window.__clockEvents)",
            isolatedContext: false);
        Assert.Equal(1, Count(events.Value.StringValue ?? string.Empty, "timeout"));
        Assert.Equal(4, Count(events.Value.StringValue ?? string.Empty, "interval"));

        await page.Clock.PauseAtAsync(pausedAt.AddSeconds(20));
        Assert.Equal(
            pausedAt.AddSeconds(20).ToUnixTimeMilliseconds(),
            await DateNowAsync(page));

        DateTimeOffset fixedTime = new(2030, 5, 6, 7, 8, 9, TimeSpan.Zero);
        await page.Clock.SetFixedTimeAsync(fixedTime);
        Assert.Equal(fixedTime.ToUnixTimeMilliseconds(), await DateNowAsync(page));
        await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(50));
        Assert.Equal(fixedTime.ToUnixTimeMilliseconds(), await DateNowAsync(page));

        DateTimeOffset systemTime = new(2031, 6, 7, 8, 9, 10, TimeSpan.Zero);
        await page.Clock.SetSystemTimeAsync(systemTime);
        Assert.Equal(systemTime.ToUnixTimeMilliseconds(), await DateNowAsync(page));

        await page.Clock.ResumeAsync();
        await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(50));
        Assert.True(await DateNowAsync(page) >= systemTime.ToUnixTimeMilliseconds());
    }

    private static async ValueTask<long> DateNowAsync(CdpPage page)
    {
        EvaluationResult result = await page.EvaluateAsync(
            "Date.now()",
            isolatedContext: false);
        Assert.True(result.Value.TryGetDouble(out double value));
        return (long)value;
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
