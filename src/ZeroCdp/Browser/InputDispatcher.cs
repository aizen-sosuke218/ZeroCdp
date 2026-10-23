using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

/// <summary>
/// Sends trusted input events through CDP.
/// </summary>
/// <remarks>
/// These are real browser input events, not script-dispatched ones, so <c>isTrusted</c> is true
/// and the page cannot tell them apart from a user's by inspecting the event object.
/// </remarks>
internal sealed class InputDispatcher
{
    private readonly CdpSession _session;

    public InputDispatcher(CdpSession session) => _session = session;

    public async ValueTask ClickAsync(double x, double y, CancellationToken cancellationToken)
        => await ClickAsync(x, y, "left", clickCount: 1, delay: null, modifiers: 0, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask ClickAsync(
        double x,
        double y,
        string button,
        int clickCount,
        TimeSpan? delay,
        int modifiers,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clickCount, 1);

        // Move first: the page sees a hover, and Chrome positions its own state before the press.
        await SendMouseAsync(DispatchMouseEventParams.Moved(x, y, modifiers), cancellationToken).ConfigureAwait(false);

        for (int i = 1; i <= clickCount; i++)
        {
            await SendMouseAsync(
                    DispatchMouseEventParams.Pressed(x, y, button, i, modifiers),
                    cancellationToken)
                .ConfigureAwait(false);

            if (delay is { } pause && pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }

            await SendMouseAsync(
                    DispatchMouseEventParams.Released(x, y, button, i, modifiers),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask TapAsync(double x, double y, CancellationToken cancellationToken)
        => TapAsync(x, y, modifiers: 0, cancellationToken);

    public async ValueTask TapAsync(
        double x,
        double y,
        int modifiers,
        CancellationToken cancellationToken)
    {
        await _session
            .SendAsync<Unit, DispatchTouchEventParams>(
                "Input.dispatchTouchEvent"u8,
                new DispatchTouchEventParams("touchStart", x, y, modifiers),
                cancellationToken)
            .ConfigureAwait(false);
        await _session
            .SendAsync<Unit, DispatchTouchEventParams>(
                "Input.dispatchTouchEvent"u8,
                new DispatchTouchEventParams("touchEnd", x, y, modifiers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask HoverAsync(double x, double y, CancellationToken cancellationToken)
        => HoverAsync(x, y, modifiers: 0, cancellationToken);

    public async ValueTask HoverAsync(
        double x,
        double y,
        int modifiers,
        CancellationToken cancellationToken)
        => await SendMouseAsync(DispatchMouseEventParams.Moved(x, y, modifiers), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Drags with the left button from one viewport point to another.</summary>
    public async ValueTask DragAsync(
        double fromX,
        double fromY,
        double toX,
        double toY,
        CancellationToken cancellationToken)
    {
        await SendMouseAsync(
                DispatchMouseEventParams.Moved(fromX, fromY),
                cancellationToken)
            .ConfigureAwait(false);
        await SendMouseAsync(
                DispatchMouseEventParams.Pressed(fromX, fromY),
                cancellationToken)
            .ConfigureAwait(false);

        const int Steps = 8;
        for (int step = 1; step <= Steps; step++)
        {
            double progress = step / (double)Steps;
            double x = fromX + ((toX - fromX) * progress);
            double y = fromY + ((toY - fromY) * progress);
            await SendMouseAsync(
                    DispatchMouseEventParams.Dragged(x, y),
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
        }

        await SendMouseAsync(
                DispatchMouseEventParams.Released(toX, toY),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Presses a key without releasing it.</summary>
    public async ValueTask KeyDownAsync(string key, CancellationToken cancellationToken)
    {
        KeyStroke stroke = KeyNames.Parse(key);
        await SendKeyAsync(
                new DispatchKeyEventParams(
                    "keyDown",
                    stroke.Key,
                    stroke.Text,
                    stroke.VirtualKeyCode,
                    stroke.Code,
                    stroke.Modifiers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Releases a key previously pressed with <see cref="KeyDownAsync"/>.</summary>
    public async ValueTask KeyUpAsync(string key, CancellationToken cancellationToken)
    {
        KeyStroke stroke = KeyNames.Parse(key);
        await SendKeyAsync(
                new DispatchKeyEventParams(
                    "keyUp",
                    stroke.Key,
                    null,
                    stroke.VirtualKeyCode,
                    stroke.Code,
                    stroke.Modifiers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask MoveAsync(double x, double y, CancellationToken cancellationToken)
        => await SendMouseAsync(DispatchMouseEventParams.Moved(x, y), cancellationToken).ConfigureAwait(false);

    public async ValueTask MouseDownAsync(
        double x,
        double y,
        string button,
        int clickCount,
        CancellationToken cancellationToken)
        => await SendMouseAsync(
                DispatchMouseEventParams.Pressed(x, y, button, clickCount),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask MouseUpAsync(
        double x,
        double y,
        string button,
        int clickCount,
        CancellationToken cancellationToken)
        => await SendMouseAsync(
                DispatchMouseEventParams.Released(x, y, button, clickCount),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask InsertTextAsync(string text, CancellationToken cancellationToken)
        => await _session.SendAsync<Unit, InsertTextParams>(
                "Input.insertText"u8,
                new InsertTextParams(text),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Presses one key or chord, for example <c>Enter</c>, <c>Tab</c> or <c>Control+A</c>.
    /// </summary>
    public ValueTask PressAsync(string key, CancellationToken cancellationToken)
        => PressAsync(key, delay: null, cancellationToken);

    /// <summary>Presses a key or chord, optionally holding it for <paramref name="delay"/>.</summary>
    public async ValueTask PressAsync(string key, TimeSpan? delay, CancellationToken cancellationToken)
    {
        KeyStroke stroke = KeyNames.Parse(key);

        await SendKeyAsync(
                new DispatchKeyEventParams(
                    "keyDown",
                    stroke.Key,
                    stroke.Text,
                    stroke.VirtualKeyCode,
                    stroke.Code,
                    stroke.Modifiers),
                cancellationToken)
            .ConfigureAwait(false);

        if (delay is { } hold && hold > TimeSpan.Zero)
        {
            await Task.Delay(hold, cancellationToken).ConfigureAwait(false);
        }

        await SendKeyAsync(
                new DispatchKeyEventParams(
                    "keyUp",
                    stroke.Key,
                    null,
                    stroke.VirtualKeyCode,
                    stroke.Code,
                    stroke.Modifiers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask ScrollAsync(double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken)
        => await SendMouseAsync(DispatchMouseEventParams.Wheel(x, y, deltaX, deltaY), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Types <paramref name="text"/> one character at a time, producing real
    /// <c>keydown</c>/<c>input</c>/<c>keyup</c> sequences.
    /// </summary>
    public async ValueTask TypeAsync(string text, TimeSpan? delay, CancellationToken cancellationToken)
    {
        foreach (char c in text)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (c is '\n' or '\r')
            {
                await SendKeyAsync(new DispatchKeyEventParams("keyDown", "Enter", "\r", 13, "Enter"), cancellationToken).ConfigureAwait(false);
                await SendKeyAsync(new DispatchKeyEventParams("keyUp", "Enter", null, 13, "Enter"), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                KeyStroke stroke = KeyNames.ForCharacter(c, shift: false, modifiers: 0);
                await SendKeyAsync(
                        new DispatchKeyEventParams(
                            "keyDown",
                            stroke.Key,
                            stroke.Text,
                            stroke.VirtualKeyCode,
                            stroke.Code,
                            0),
                        cancellationToken)
                    .ConfigureAwait(false);

                await SendKeyAsync(
                        new DispatchKeyEventParams(
                            "keyUp",
                            stroke.Key,
                            null,
                            stroke.VirtualKeyCode,
                            stroke.Code,
                            0),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (delay is { } pause && pause > TimeSpan.Zero)
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private ValueTask<Unit> SendMouseAsync(DispatchMouseEventParams parameters, CancellationToken cancellationToken)
        => _session.SendAsync<Unit, DispatchMouseEventParams>("Input.dispatchMouseEvent"u8, parameters, cancellationToken);

    private ValueTask<Unit> SendKeyAsync(DispatchKeyEventParams parameters, CancellationToken cancellationToken)
        => _session.SendAsync<Unit, DispatchKeyEventParams>("Input.dispatchKeyEvent"u8, parameters, cancellationToken);
}
