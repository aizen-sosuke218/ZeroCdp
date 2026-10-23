using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

/// <summary>
/// Decides whether a browser-backed test runs.
/// </summary>
/// <remarks>
/// The default keeps the suite friendly to machines without Chromium: the test returns early.
/// Setting <c>ZEROCDP_REQUIRE_BROWSER=1</c> turns a missing browser into a failure, which is how
/// the Chromium conformance run is executed in CI (and by
/// <c>scripts/test-chromium.sh</c>). A test must never skip silently in that mode.
/// </remarks>
internal static class TestBrowser
{
    public static bool Require()
    {
        if (BrowserLocator.Find() is not null)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("ZEROCDP_REQUIRE_BROWSER") == "1")
        {
            throw new InvalidOperationException(
                "ZEROCDP_REQUIRE_BROWSER=1 but no Chromium-family browser was found. " +
                "Set ZEROCDP_CHROME_PATH to the browser executable.");
        }

        return false;
    }
}
