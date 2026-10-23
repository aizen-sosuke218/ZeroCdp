namespace ZeroCdp.Browser;

/// <summary>
/// Playwright-style keyboard device. All events are trusted CDP input events.
/// </summary>
public sealed class Keyboard
{
    private readonly InputDispatcher _input;

    internal Keyboard(InputDispatcher input) => _input = input;

    /// <summary>Presses and releases one key or chord, for example <c>Control+A</c>.</summary>
    public ValueTask PressAsync(string key, CancellationToken cancellationToken = default)
        => _input.PressAsync(key, cancellationToken);

    /// <summary>Presses a key and leaves it down until <see cref="UpAsync"/>.</summary>
    public ValueTask DownAsync(string key, CancellationToken cancellationToken = default)
        => _input.KeyDownAsync(key, cancellationToken);

    /// <summary>Releases a key pressed with <see cref="DownAsync"/>.</summary>
    public ValueTask UpAsync(string key, CancellationToken cancellationToken = default)
        => _input.KeyUpAsync(key, cancellationToken);

    /// <summary>Types text as a sequence of real key events.</summary>
    public ValueTask TypeAsync(
        string text,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
        => _input.TypeAsync(text, delay, cancellationToken);

    /// <summary>Inserts text without producing key events (useful for IME-style input).</summary>
    public ValueTask InsertTextAsync(string text, CancellationToken cancellationToken = default)
        => _input.InsertTextAsync(text, cancellationToken);
}

/// <summary>Playwright-style touchscreen device.</summary>
public sealed class Touchscreen
{
    private readonly InputDispatcher _input;

    internal Touchscreen(InputDispatcher input) => _input = input;

    /// <summary>Taps a viewport coordinate with a trusted touch sequence.</summary>
    public ValueTask TapAsync(double x, double y, CancellationToken cancellationToken = default)
        => _input.TapAsync(x, y, cancellationToken);

    internal ValueTask TapAsync(
        double x,
        double y,
        int modifiers,
        CancellationToken cancellationToken)
        => _input.TapAsync(x, y, modifiers, cancellationToken);
}

/// <summary>
/// Playwright-style mouse device, tracking the current pointer position the way Playwright does.
/// </summary>
public sealed class Mouse
{
    private readonly InputDispatcher _input;
    private double _x;
    private double _y;

    internal Mouse(InputDispatcher input) => _input = input;

    /// <summary>Moves the pointer to a viewport coordinate.</summary>
    public async ValueTask MoveAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        _x = x;
        _y = y;
        await _input.MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Presses a button at the current pointer position.</summary>
    public ValueTask DownAsync(
        MouseButton button = MouseButton.Left,
        int clickCount = 1,
        CancellationToken cancellationToken = default)
        => _input.MouseDownAsync(_x, _y, ButtonName(button), clickCount, cancellationToken);

    /// <summary>Releases a button at the current pointer position.</summary>
    public ValueTask UpAsync(
        MouseButton button = MouseButton.Left,
        int clickCount = 1,
        CancellationToken cancellationToken = default)
        => _input.MouseUpAsync(_x, _y, ButtonName(button), clickCount, cancellationToken);

    /// <summary>Moves to a point and performs one click sequence there.</summary>
    public async ValueTask ClickAsync(
        double x,
        double y,
        MouseButton button = MouseButton.Left,
        int clickCount = 1,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        _x = x;
        _y = y;
        await _input
            .ClickAsync(x, y, ButtonName(button), clickCount, delay, modifiers: 0, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Scrolls at the current pointer position.</summary>
    public ValueTask WheelAsync(
        double deltaX,
        double deltaY,
        CancellationToken cancellationToken = default)
        => _input.ScrollAsync(_x, _y, deltaX, deltaY, cancellationToken);

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        _ => "left",
    };
}
