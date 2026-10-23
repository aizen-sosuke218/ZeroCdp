using System.Buffers.Binary;
using System.Text;
using ZeroCdp.Browser;
using ZeroCdp.Protocol;
using Xunit;

namespace ZeroCdp.Tests;

public class LocatorErgonomicsTests : IAsyncLifetime
{
    private string _directory = string.Empty;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "zerocdp-locator-" + Guid.NewGuid().ToString("N"));
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
    public async Task Nth_first_last_text_attribute_and_input_value_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "items.html",
            """
            <ul>
              <li class="item" data-id="a">one</li>
              <li class="item" data-id="b">two</li>
              <li class="item" data-id="c">three</li>
            </ul>
            <input id="name" value="initial">
            <div id="rendered"> visible <span>text</span> </div>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator items = page.Locator(".item");
        Assert.Equal("one", await items.First().TextAsync());
        Assert.Equal("three", await items.Last().TextAsync());
        Assert.Equal("two", await items.Nth(1).TextAsync());
        Assert.Equal(1, await items.Nth(1).CountAsync());
        Assert.Equal(["one", "two", "three"], await items.AllTextContentsAsync());
        Assert.Equal("b", await items.Nth(1).GetAttributeAsync("data-id"));
        Assert.Null(await items.First().GetAttributeAsync("missing"));
        Assert.Equal("initial", await page.Locator("#name").InputValueAsync());
        Assert.Contains("visible", await page.Locator("#rendered").InnerTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_options_support_button_modifiers_and_double_click()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "clicks.html",
            """
            <button id="target" style="width:160px;height:40px">click</button>
            <script>
              window.events = [];
              const target = document.getElementById('target');
              target.addEventListener('contextmenu', event => {
                event.preventDefault();
                window.events.push('context:' + event.button);
              });
              target.addEventListener('click', event => {
                window.events.push('click:' + event.ctrlKey + ':' + event.detail);
              });
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator target = page.Locator("#target");
        await target.ClickAsync(new ClickOptions { Modifiers = KeyModifiers.Control });
        await target.ClickAsync(new ClickOptions { Button = MouseButton.Right });
        await target.DblClickAsync();

        EvaluationResult events = await page.EvaluateAsync("window.events.join(',')", isolatedContext: false);
        string text = events.Value.StringValue ?? string.Empty;
        Assert.Contains("click:true", text, StringComparison.Ordinal);
        Assert.Contains("context:2", text, StringComparison.Ordinal);
        Assert.Contains("click:false:2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normal_locator_reaches_closed_shadow_root()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "closed-shadow.html",
            """
            <div id="host"></div>
            <script>
              const root = document.getElementById('host').attachShadow({ mode: 'closed' });
              root.innerHTML = '<button id="inside">closed-root</button><span class="shadow-item">a</span><span class="shadow-item">b</span>';
              root.getElementById('inside').addEventListener('click', () => { window.closedClicked = true; });
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator inside = page.Locator("#inside");
        Assert.Equal("closed-root", await inside.TextAsync());
        Assert.Equal(["a", "b"], await page.Locator(".shadow-item").AllTextContentsAsync());

        await inside.ClickAsync();
        EvaluationResult clicked = await page.EvaluateAsync("window.closedClicked === true", isolatedContext: false);
        Assert.True(clicked.Value.TryGetBoolean(out bool value));
        Assert.True(value);
    }

    [Fact]
    public async Task Main_world_evaluation_is_opt_in_and_isolated_by_default()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write("main-world.html", "<div id='value'>main</div>");
        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        await page.EvaluateAsync("window.mainWorldMarker = 42", isolatedContext: false);
        EvaluationResult main = await page.EvaluateAsync("window.mainWorldMarker", isolatedContext: false);
        Assert.True(main.Value.TryGetInt64(out long marker));
        Assert.Equal(42, marker);

        EvaluationResult isolated = await page.EvaluateAsync("typeof window.mainWorldMarker");
        Assert.Equal("undefined", isolated.Value.StringValue);
    }

    [Fact]
    public async Task Element_screenshot_is_clipped_to_the_element_box()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "screenshot.html",
            "<div id='box' style='width:40px;height:30px;background:#f00'></div>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        byte[] png = await page.Locator("#box").ScreenshotBytesAsync();
        Assert.Equal(0x89, png[0]);
        Assert.Equal("PNG", Encoding.ASCII.GetString(png, 1, 3));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(40, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    [Fact]
    public async Task Semantic_locators_match_text_role_label_placeholder_and_test_id()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "semantic.html",
            """
            <button id="save">Save changes</button>
            <button id="close" aria-label="Close dialog">x</button>
            <label for="email">Email address</label>
            <input id="email" placeholder="you@example.com">
            <input id="search" placeholder="Search docs">
            <div data-testid="card"><span>Alpha</span><span>Beta</span></div>
            <div id="host"></div>
            <script>
              const root = document.getElementById('host').attachShadow({ mode: 'closed' });
              root.innerHTML = '<button aria-label="Shadow save">Shadow save</button>';
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Assert.Equal("Save changes", await page.GetByText("Save changes").TextAsync());
        Assert.Equal(0, await page.GetByText("Save", exact: true).CountAsync());
        Assert.Equal(1, await page.GetByRole("button", "Save changes").CountAsync());
        Assert.Equal(1, await page.GetByRole("button", "Close dialog").CountAsync());
        Assert.Equal(1, await page.GetByLabel("Email address").CountAsync());
        Assert.Equal("email", await page.GetByLabel("Email address").GetAttributeAsync("id"));
        Assert.Equal(1, await page.GetByPlaceholder("Search docs").CountAsync());
        Assert.Equal("search", await page.GetByPlaceholder("Search docs").GetAttributeAsync("id"));
        Assert.Equal(0, await page.GetByLabel("Search docs").CountAsync());
        Assert.Equal(1, await page.GetByTestId("card").CountAsync());
        Assert.Equal(1, await page.GetByText("Beta").CountAsync());

        // The semantic engine uses a pierced snapshot, so closed roots are included too.
        Assert.Equal(1, await page.GetByText("Shadow save").CountAsync());
        Assert.Equal(1, await page.GetByRole("button", "Shadow save").CountAsync());
    }

    [Fact]
    public async Task Locator_state_box_and_focus_match_playwright_semantics()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "state.html",
            """
            <style>body { margin: 0; }</style>
            <button id="box" style="position:absolute;left:10px;top:20px;width:100px;height:30px">Box</button>
            <input id="disabled" disabled>
            <input id="readonly" readonly value="fixed">
            <input id="check" type="checkbox" checked>
            <input id="focus">
            <div id="hidden" style="display:none">hidden</div>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        BoundingBox box = await page.Locator("#box").BoundingBoxAsync()
            ?? throw new InvalidOperationException("Expected a box.");
        Assert.InRange(box.X, 9, 11);
        Assert.InRange(box.Y, 19, 21);
        Assert.InRange(box.Width, 99, 101);
        Assert.InRange(box.Height, 29, 31);
        Assert.Null(await page.Locator("#hidden").BoundingBoxAsync());

        Assert.True(await page.Locator("#box").IsEnabledAsync());
        Assert.False(await page.Locator("#disabled").IsEnabledAsync());
        Assert.True(await page.Locator("#focus").IsEditableAsync());
        Assert.False(await page.Locator("#readonly").IsEditableAsync());
        Assert.True(await page.Locator("#check").IsCheckedAsync());
        Assert.True(await page.Locator("#hidden").IsHiddenAsync());
        Assert.False(await page.Locator("#box").IsHiddenAsync());

        await page.Locator("#focus").FocusAsync();
        EvaluationResult focused = await page.EvaluateAsync("document.activeElement.id");
        Assert.Equal("focus", focused.Value.StringValue);
    }

    [Fact]
    public async Task Locator_editing_evaluation_and_event_helpers_work()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        string file = Write(
            "locator-api.html",
            """
            <input id="name" value="alpha">
            <div id="box">content</div>
            <img id="logo" alt="Brand logo">
            <span id="tip" title="Tooltip text"></span>
            <input id="agree" type="checkbox">
            <button id="emit">emit</button>
            <div id="drag" draggable="true" style="width:80px;height:30px;background:#ddd">drag</div>
            <div id="drop" style="width:140px;height:60px;background:#eee">drop</div>
            <script>
              document.getElementById('emit').addEventListener('custom-event', event => {
                document.body.dataset.eventState = event.type + ':' + event.bubbles + ':' + event.cancelable;
              });
              const drag = document.getElementById('drag');
              const drop = document.getElementById('drop');
              drag.addEventListener('dragstart', event => event.dataTransfer.setData('text/plain', 'drag'));
              drop.addEventListener('dragover', event => event.preventDefault());
              drop.addEventListener('drop', event => {
                event.preventDefault();
                drop.textContent = 'dropped';
              });
            </script>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);

        Locator input = page.Locator("#name");
        await input.SelectTextAsync();
        EvaluationResult selection = await page.EvaluateAsync(
            "document.getElementById('name').selectionStart + ':' + document.getElementById('name').selectionEnd");
        Assert.Equal("0:5", selection.Value.StringValue);

        await input.ClearAsync();
        Assert.Equal(string.Empty, await input.InputValueAsync());

        await input.PressSequentiallyAsync("ab", TimeSpan.Zero);
        Assert.Equal("ab", await input.InputValueAsync());

        await input.FocusAsync();
        await input.BlurAsync();
        EvaluationResult blurred = await page.EvaluateAsync("document.activeElement.id !== 'name'");
        Assert.True(blurred.Value.TryGetBoolean(out bool focusLeft));
        Assert.True(focusLeft);

        Locator box = page.Locator("#box");
        EvaluationResult evaluated = await box.EvaluateAsync("(element) => element.textContent");
        Assert.Equal("content", evaluated.Value.StringValue);
        Assert.Equal("content", await box.TextContentAsync());

        Locator emit = page.Locator("#emit");
        Assert.True(await emit.DispatchEventAsync("custom-event", bubbles: true, cancelable: false));
        EvaluationResult eventState = await page.EvaluateAsync("document.body.dataset.eventState");
        Assert.Equal("custom-event:true:false", eventState.Value.StringValue);

        await box.ScrollIntoViewIfNeededAsync();
        Assert.Equal(["content"], await page.Locator("#box").AllTextContentsAsync());
        Assert.Equal(1, await page.GetByAltText("Brand logo").CountAsync());
        Assert.Equal(1, await page.GetByTitle("Tooltip text").CountAsync());

        Locator agree = page.Locator("#agree");
        await agree.SetCheckedAsync(true);
        Assert.True(await agree.IsCheckedAsync());
        await agree.SetCheckedAsync(false);
        Assert.False(await agree.IsCheckedAsync());

        await page.Locator("#drag").DragToAsync(page.Locator("#drop"));
        await page.WaitForFunctionAsync("document.getElementById('drop').textContent === 'dropped'");

        await page.EvaluateAsync(
            "setTimeout(() => { document.body.dataset.ready = 'yes'; }, 50)");
        await page.WaitForFunctionAsync("document.body.dataset.ready === 'yes'");
        Locator waited = await page.WaitForSelectorAsync("#box");
        Assert.Equal("#box", waited.Selector);
        await page.WaitForTimeoutAsync(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task Frame_locator_reaches_closed_shadow_and_semantic_matches()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        Write(
            "closed-frame.html",
            """
            <div id="host"></div>
            <button id="save">Frame save</button>
            <label for="email">Frame email</label>
            <input id="email" placeholder="Frame placeholder">
            <div data-testid="frame-card">Frame card</div>
            <script>
              const root = document.getElementById('host').attachShadow({ mode: 'closed' });
              root.innerHTML = '<button id="inside">inside-frame</button><span>Frame shadow</span>';
              root.getElementById('inside').addEventListener('click', () => { document.body.setAttribute('data-frame-clicked', 'yes'); });
            </script>
            """);

        string parent = Write(
            "frame-parent.html",
            "<iframe id='frame' src='closed-frame.html'></iframe>");

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(parent).AbsoluteUri);
        await page.WaitForLoadStateAsync(WaitUntil.Load);

        FrameLocator frame = page.FrameLocator("#frame");
        Assert.Equal("inside-frame", await frame.Locator("#inside").TextAsync());
        Assert.Equal(1, await frame.Locator("#inside", pierceShadowDom: true).CountAsync());

        Assert.Equal(1, await frame.GetByText("Frame shadow").CountAsync());
        Assert.Equal(1, await frame.GetByText("Frame shadow").First().CountAsync());
        Assert.Equal(1, await frame.GetByRole("button", "Frame save").CountAsync());
        Assert.Equal(1, await frame.GetByLabel("Frame email").CountAsync());
        Assert.Equal(1, await frame.GetByPlaceholder("Frame placeholder").CountAsync());
        Assert.Equal(1, await frame.GetByTestId("frame-card").CountAsync());

        await frame.Locator("#inside").ClickAsync();
        EvaluationResult clicked = await frame.EvaluateAsync(
            "document.body.getAttribute('data-frame-clicked')");
        Assert.Equal("yes", clicked.Value.StringValue);
    }

    [Fact]
    public async Task Locator_chaining_scopes_css_semantic_shadow_and_frame_queries()
    {
        if (BrowserLocator.Find() is null)
        {
            return;
        }

        const string FrameName = "chain-frame.html";
        Write(
            FrameName,
            """
            <div class="card">
              <span class="label">frame-label</span>
              <button class="action">Frame action</button>
            </div>
            <div id="frame-host"></div>
            <script>
              const host = document.getElementById('frame-host');
              const root = host.attachShadow({ mode: 'closed' });
              root.innerHTML = '<span class="deep">frame-deep</span>' +
                '<button class="action" aria-label="Frame shadow action">Frame shadow</button>';
              document.querySelector('.card .action').addEventListener('click', () => {
                document.body.setAttribute('data-chain-clicked', 'yes');
              });
            </script>
            """);

        string file = Write(
            "chain-parent.html",
            $$"""
            <div class="card">
              <span class="label">one</span>
              <button class="action">First</button>
            </div>
            <div class="card">
              <span class="label">two</span>
              <button class="action">Second</button>
            </div>
            <div id="host"></div>
            <script>
              const root = document.getElementById('host').attachShadow({ mode: 'closed' });
              root.innerHTML = '<div class="inner"><span class="deep">shadow-deep</span>' +
                '<button class="action" aria-label="Shadow action">Shadow action</button></div>';
              root.querySelector('.action').addEventListener('click', () => {
                window.shadowClicked = true;
              });
              document.querySelectorAll('.card .action').forEach((button, index) => {
                button.addEventListener('click', () => { window.chainClicked = String(index); });
              });
            </script>
            <iframe id="frame" src="{{FrameName}}"></iframe>
            """);

        await using LaunchedBrowser browser = await BrowserLauncher.LaunchAsync(
            new LaunchOptions { Headless = true });
        CdpPage page = await browser.Browser.NewPageAsync();
        await page.GotoAsync(new Uri(file).AbsoluteUri);
        await page.WaitForLoadStateAsync(WaitUntil.Load);

        Locator cards = page.Locator(".card");
        Assert.Equal(2, await cards.CountAsync());
        Assert.Equal(["one", "two"], await cards.Locator(".label").AllTextContentsAsync());
        Assert.Equal("two", await cards.Nth(1).Locator(".label").TextAsync());
        Assert.Equal(1, await cards.First().Locator(".label").CountAsync());
        Assert.Equal(2, await cards.GetByRole("button").CountAsync());
        Assert.Equal("Second", await cards.Nth(1).GetByRole("button").TextAsync());
        Assert.Equal(1, await cards.GetByText("two", exact: true).CountAsync());

        Locator host = page.Locator("#host");
        Assert.Equal("shadow-deep", await host.Locator(".deep").TextAsync());
        Assert.Equal(1, await host.GetByRole("button", "Shadow action").CountAsync());

        FrameLocator frame = page.FrameLocator("#frame");
        Assert.Equal("frame-label", await frame.Locator(".card").Locator(".label").TextAsync());
        Assert.Equal("frame-deep", await frame.Locator("#frame-host").Locator(".deep").TextAsync());
        Assert.Equal(1, await frame.Locator(".card").GetByRole("button", "Frame action").CountAsync());

        await cards.Nth(1).Locator(".action").ClickAsync();
        EvaluationResult clicked = await page.EvaluateAsync("window.chainClicked", isolatedContext: false);
        Assert.Equal("1", clicked.Value.StringValue);

        await host.Locator(".action").ClickAsync();
        EvaluationResult shadowClicked = await page.EvaluateAsync(
            "window.shadowClicked === true",
            isolatedContext: false);
        Assert.True(shadowClicked.Value.TryGetBoolean(out bool shadow) && shadow);

        await frame.Locator(".card").Locator(".action").ClickAsync();
        EvaluationResult frameClicked = await frame.EvaluateAsync(
            "document.body.getAttribute('data-chain-clicked')");
        Assert.Equal("yes", frameClicked.Value.StringValue);
    }

    private string Write(string name, string body)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(
            path,
            $"<!doctype html><html><head><title>test</title></head><body>{body}</body></html>",
            Encoding.UTF8);
        return path;
    }
}
