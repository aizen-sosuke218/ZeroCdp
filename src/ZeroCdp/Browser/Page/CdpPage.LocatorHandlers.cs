namespace ZeroCdp.Browser;

/// <summary>Options for <see cref="CdpPage.AddLocatorHandlerAsync"/>.</summary>
public sealed class PageAddLocatorHandlerOptions
{
    /// <summary>Reserved for Playwright compatibility.</summary>
    public bool NoWaitAfter { get; init; }

    /// <summary>Maximum time the handler may take, or null for the page default.</summary>
    public TimeSpan? Timeout { get; init; }
}

public sealed partial class CdpPage
{
    private static readonly AsyncLocal<int> LocatorHandlerDepth = new();

    private readonly Lock _locatorHandlerGate = new();
    private readonly List<LocatorHandlerRegistration> _locatorHandlers = new();

    /// <summary>
    /// Registers a handler that runs before locator actions when <paramref name="locator"/> is
    /// visible. This is the Playwright escape hatch for cookie banners and similar overlays.
    /// </summary>
    public ValueTask AddLocatorHandlerAsync(
        Locator locator,
        Func<Task> handler,
        PageAddLocatorHandlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(IsClosed, this);

        lock (_locatorHandlerGate)
        {
            _locatorHandlers.Add(new LocatorHandlerRegistration(locator, handler, options));
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Playwright-style async alias for <see cref="RemoveLocatorHandler"/>.</summary>
    public ValueTask RemoveLocatorHandlerAsync(Locator locator)
    {
        RemoveLocatorHandler(locator);
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes a handler previously registered for <paramref name="locator"/>.</summary>
    public void RemoveLocatorHandler(Locator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        lock (_locatorHandlerGate)
        {
            _locatorHandlers.RemoveAll(registration => ReferenceEquals(registration.Locator, locator));
        }
    }

    internal async ValueTask RunLocatorHandlersAsync(
        Locator target,
        CancellationToken cancellationToken)
    {
        if (LocatorHandlerDepth.Value != 0)
        {
            return;
        }

        LocatorHandlerRegistration[] handlers;
        lock (_locatorHandlerGate)
        {
            if (_locatorHandlers.Count == 0)
            {
                return;
            }

            handlers = _locatorHandlers.ToArray();
        }

        LocatorHandlerDepth.Value++;
        try
        {
            foreach (LocatorHandlerRegistration registration in handlers)
            {
                if (ReferenceEquals(registration.Locator, target))
                {
                    continue;
                }

                if (await registration.Locator.IsVisibleAsync(cancellationToken).ConfigureAwait(false))
                {
                    await registration.Handler().WaitAsync(
                            registration.Options?.Timeout ?? DefaultTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            LocatorHandlerDepth.Value--;
        }
    }

    private sealed record LocatorHandlerRegistration(
        Locator Locator,
        Func<Task> Handler,
        PageAddLocatorHandlerOptions? Options);
}
