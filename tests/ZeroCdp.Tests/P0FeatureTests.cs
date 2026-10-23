using System.Text;
using ZeroCdp.Browser;
using Xunit;

namespace ZeroCdp.Tests;

public class P0FeatureTests : IAsyncLifetime
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-p0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Captures_viewport_jpeg_and_full_page_screenshots()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "shot.html",
            "<div style=\"height:2400px;width:420px;background:linear-gradient(red,blue)\">tall</div>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        string viewportPath = Path.Combine(_directory, "shots", "viewport.png");
        await page.ScreenshotAsync(viewportPath);
        byte[] viewport = await File.ReadAllBytesAsync(viewportPath);

        Assert.True(viewport.Length > 100);
        Assert.True(viewport.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));

        byte[] fullPage = await page.ScreenshotBytesAsync(new ScreenshotOptions { FullPage = true });
        Assert.True(fullPage.Length > viewport.Length);
        Assert.True(fullPage.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));

        byte[] jpeg = await page.ScreenshotBytesAsync(new ScreenshotOptions
        {
            Format = ScreenshotFormat.Jpeg,
            Quality = 60,
        });
        Assert.True(jpeg.Length > 100);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    [Fact]
    public async Task Rejects_a_full_page_screenshot_above_the_pixel_cap()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write("too-tall.html", "<div style=\"height:20000px\">tall</div>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await Assert.ThrowsAsync<CdpResourceLimitException>(
            () => page.ScreenshotBytesAsync(new ScreenshotOptions { FullPage = true }).AsTask());
    }

    [Fact]
    public async Task Missing_iframe_times_out_with_the_configured_timeout()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write("no-frame.html", "<p>no frame</p>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator missing = page.FrameLocator("#absent").Locator("#child", timeout: TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<CdpTimeoutException>(() => missing.ClickAsync().AsTask());
    }

    [Fact]
    public async Task Auto_dismisses_an_alert_opened_by_a_click()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "alert.html",
            "<button id=\"go\" onclick=\"alert('blocked'); document.title='after-alert'\">Go</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#go").ClickAsync();

        Assert.Equal("after-alert", await WaitForTitleAsync(page, "after-alert"));
        Assert.False(page.IsDialogOpen);
    }

    [Fact]
    public async Task Accepts_a_prompt_through_the_dialog_event()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "prompt.html",
            "<button id=\"go\" onclick=\"document.title = prompt('Your name?', 'default') || 'cancelled'\">Ask</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        page.AutoDismissDialogs = false;
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        TaskCompletionSource<DialogInfo> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.DialogOpening += dialog => opened.TrySetResult(dialog);

        Task click = page.Locator("#go").ClickAsync().AsTask();
        DialogInfo info = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DialogType.Prompt, info.Type);
        Assert.Equal("Your name?", info.Message);
        Assert.Equal("default", info.DefaultPrompt);
        Assert.True(page.IsDialogOpen);

        Assert.True(await page.AcceptDialogAsync("alice"));
        await click;
        Assert.Equal("alice", await WaitForTitleAsync(page, "alice"));
    }

    [Fact]
    public async Task Clicks_and_evaluates_inside_an_iframe()
    {
        if (!HasBrowser())
        {
            return;
        }
        WriteChild("inner.html", "<title>inner</title><button id=\"go\" onclick=\"document.title='frame-clicked'\">Go</button>");
        string outer = Write(
            "outer.html",
            "<iframe id=\"frame\" src=\"inner.html\" style=\"width:420px;height:220px\"></iframe>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(outer).AbsoluteUri);

        FrameLocator frame = page.FrameLocator("#frame");

        Assert.Equal("inner", (await frame.EvaluateAsync("document.title")).Value.StringValue);
        Assert.Equal(1, await frame.Locator("#go").CountAsync());

        await frame.Locator("#go").ClickAsync();

        string innerTitle = string.Empty;
        for (int i = 0; i < 100; i++)
        {
            innerTitle = (await frame.EvaluateAsync("document.title")).Value.StringValue ?? string.Empty;
            if (innerTitle == "frame-clicked")
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal("frame-clicked", innerTitle);
    }

    [Fact]
    public async Task Waits_for_a_late_element_before_clicking()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "late.html",
            """
            <div id="host"></div>
            <script>
              setTimeout(() => {
                const button = document.createElement('button');
                button.id = 'late';
                button.textContent = 'Late';
                button.onclick = () => { document.title = 'late-clicked'; };
                document.getElementById('host').appendChild(button);
              }, 350);
            </script>
            """);

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#late").ClickAsync();
        Assert.Equal("late-clicked", await WaitForTitleAsync(page, "late-clicked"));
    }

    [Fact]
    public async Task Missing_element_times_out_with_the_configured_timeout()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write("empty.html", "<p>nothing</p>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator missing = page.Locator("#absent", timeout: TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<CdpTimeoutException>(() => missing.ClickAsync().AsTask());
    }

    [Fact]
    public async Task Fill_replaces_existing_text_and_fires_input_events()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "fill.html",
            "<input id=\"field\" value=\"old\" oninput=\"document.title='got:'+this.value\">");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator field = page.Locator("#field");
        await field.FillAsync("hello");
        Assert.Equal("got:hello", await WaitForTitleAsync(page, "got:hello"));

        await field.FillAsync("bye");
        Assert.Equal("got:bye", await WaitForTitleAsync(page, "got:bye"));
    }

    [Fact]
    public async Task Hover_dispatches_a_real_mouse_enter()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "hover.html",
            "<button id=\"target\" onmouseenter=\"document.title='hovered'\">Hover me</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#target").HoverAsync();
        Assert.Equal("hovered", await WaitForTitleAsync(page, "hovered"));
    }

    [Fact]
    public async Task Check_and_uncheck_do_not_toggle_twice()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "check.html",
            "<input id=\"box\" type=\"checkbox\" onchange=\"document.title=this.checked?'checked':'unchecked'\">");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator box = page.Locator("#box");
        await box.CheckAsync();
        Assert.Equal("checked", await WaitForTitleAsync(page, "checked"));

        await box.CheckAsync();
        Assert.Equal("checked", await page.TitleAsync());

        await box.UncheckAsync();
        Assert.Equal("unchecked", await WaitForTitleAsync(page, "unchecked"));
    }

    [Fact]
    public async Task Selects_options_by_value_or_label()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "select.html",
            """
            <select id="choice" onchange="document.title=this.value">
              <option value="a">Alpha</option>
              <option value="b">Beta</option>
            </select>
            """);

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator select = page.Locator("#choice");
        string[] selected = await select.SelectOptionAsync("b");
        Assert.Equal(["b"], selected);
        Assert.Equal("b", await WaitForTitleAsync(page, "b"));

        await select.SelectOptionAsync("Alpha");
        Assert.Equal("a", await WaitForTitleAsync(page, "a"));
    }

    [Fact]
    public async Task Press_sends_a_named_key()
    {
        if (!HasBrowser())
        {
            return;
        }
        string file = Write(
            "press.html",
            "<input id=\"field\" onkeydown=\"if(event.key==='Enter') document.title='pressed'\">");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#field").PressAsync("Enter");
        Assert.Equal("pressed", await WaitForTitleAsync(page, "pressed"));
    }

    [Fact]
    public async Task Fills_and_selects_inside_an_iframe()
    {
        if (!HasBrowser())
        {
            return;
        }

        WriteChild(
            "form-inner.html",
            """
            <title>form</title>
            <input id="field" oninput="document.title='frame:'+this.value">
            <select id="choice" onchange="document.title='choice:'+this.value">
              <option value="a">Alpha</option>
              <option value="b">Beta</option>
            </select>
            """);
        string outer = Write(
            "form-outer.html",
            "<iframe id=\"frame\" src=\"form-inner.html\" style=\"width:460px;height:240px\"></iframe>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(outer).AbsoluteUri);

        FrameLocator frame = page.FrameLocator("#frame");
        await frame.Locator("#field").FillAsync("frame-text");
        Assert.Equal("frame:frame-text", await WaitForFrameTitleAsync(frame, "frame:frame-text"));

        await frame.Locator("#choice").SelectOptionAsync("b");
        Assert.Equal("choice:b", await WaitForFrameTitleAsync(frame, "choice:b"));
    }

    [Fact]
    public async Task Clicks_inside_a_nested_iframe()
    {
        if (!HasBrowser())
        {
            return;
        }

        WriteChild(
            "deep.html",
            "<title>deep</title><button id=\"deep\" onclick=\"document.title='deep-clicked'\">Deep</button>");
        WriteChild(
            "middle.html",
            "<title>middle</title><iframe id=\"middle\" src=\"deep.html\" style=\"width:360px;height:180px\"></iframe>");
        string outer = Write(
            "nested-outer.html",
            "<iframe id=\"outer\" src=\"middle.html\" style=\"width:460px;height:260px\"></iframe>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(outer).AbsoluteUri);

        FrameLocator deep = page.FrameLocator("#outer").Frame("#middle");
        await deep.Locator("#deep").ClickAsync();

        string title = string.Empty;
        for (int i = 0; i < 100; i++)
        {
            title = (await deep.EvaluateAsync("document.title")).Value.StringValue ?? string.Empty;
            if (title == "deep-clicked")
            {
                break;
            }

            await Task.Delay(20);
        }

        Assert.Equal("deep-clicked", title);
    }

    [Fact]
    public async Task Waits_for_an_element_to_become_hidden()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "hidden.html",
            """
            <div id="target">here</div>
            <script>
              setTimeout(() => document.getElementById('target').remove(), 300);
            </script>
            """);

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Assert.True(await page.Locator("#target").IsVisibleAsync());
        await page.Locator("#target").WaitForHiddenAsync(TimeSpan.FromSeconds(5));
        Assert.False(await page.Locator("#target").IsVisibleAsync());
    }

    [Fact]
    public async Task Dismisses_a_confirm_dialog()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "confirm.html",
            "<button id=\"go\" onclick=\"document.title = confirm('sure?') ? 'yes' : 'no'\">Ask</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        page.AutoDismissDialogs = false;
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        TaskCompletionSource<DialogInfo> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.DialogOpening += dialog => opened.TrySetResult(dialog);

        Task click = page.Locator("#go").ClickAsync().AsTask();
        DialogInfo info = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DialogType.Confirm, info.Type);

        Assert.True(await page.DismissDialogAsync());
        await click;
        Assert.Equal("no", await WaitForTitleAsync(page, "no"));
    }

    [Fact]
    public async Task Waits_for_a_disabled_button_to_become_enabled()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write(
            "disabled.html",
            """
            <button id="go" disabled onclick="document.title='enabled-clicked'">Go</button>
            <script>
              setTimeout(() => document.getElementById('go').disabled = false, 350);
            </script>
            """);

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.Locator("#go").ClickAsync();
        Assert.Equal("enabled-clicked", await WaitForTitleAsync(page, "enabled-clicked"));
    }

    [Fact]
    public async Task Check_requires_a_checkbox_or_radio()
    {
        if (!HasBrowser())
        {
            return;
        }

        string file = Write("not-checkable.html", "<button id=\"go\">Go</button>");

        await using LaunchedBrowser browser = await LaunchAsync();
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await Assert.ThrowsAsync<CdpException>(() => page.Locator("#go").CheckAsync().AsTask());
    }

    [Fact]
    public void Keyboard_parses_chords_and_named_keys()
    {
        KeyStroke ctrlA = KeyNames.Parse("Control+A");
        Assert.Equal("a", ctrlA.Key);
        Assert.Equal("KeyA", ctrlA.Code);
        Assert.Equal(KeyNames.Control, ctrlA.Modifiers);
        Assert.Null(ctrlA.Text);

        KeyStroke enter = KeyNames.Parse("Enter");
        Assert.Equal("Enter", enter.Key);
        Assert.Equal(13, enter.VirtualKeyCode);
        Assert.Equal("\r", enter.Text);

        KeyStroke f5 = KeyNames.Parse("F5");
        Assert.Equal("F5", f5.Key);
        Assert.Equal(116, f5.VirtualKeyCode);

        Assert.Throws<ArgumentException>(() => KeyNames.Parse("NotAKey"));
    }

    private static bool HasBrowser() => BrowserLocator.Find() is not null;

    private static ValueTask<LaunchedBrowser> LaunchAsync()
        => BrowserLauncher.LaunchAsync(new LaunchOptions { Headless = true });

    private static async Task<string> WaitForTitleAsync(CdpPage page, string expected)
    {
        string title = string.Empty;
        for (int i = 0; i < 250; i++)
        {
            title = await page.TitleAsync();
            if (title == expected)
            {
                return title;
            }

            await Task.Delay(20);
        }

        return title;
    }

    private static async Task<string> WaitForFrameTitleAsync(FrameLocator frame, string expected)
    {
        string title = string.Empty;
        for (int i = 0; i < 250; i++)
        {
            title = (await frame.EvaluateAsync("document.title")).Value.StringValue ?? string.Empty;
            if (title == expected)
            {
                return title;
            }

            await Task.Delay(20);
        }

        return title;
    }

    private string Write(string name, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, $"<!doctype html><html><head><title>start</title></head><body>{body}</body></html>");
        return path;
    }

    private void WriteChild(string name, string body)
    {
        File.WriteAllText(
            Path.Combine(_directory, name),
            $"<!doctype html><html><head></head><body>{body}</body></html>");
    }
}
