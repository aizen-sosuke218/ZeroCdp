namespace ZeroCdp.Browser;

/// <summary>Describes where a binding call originated.</summary>
public sealed class BindingSource
{
    internal BindingSource(CdpPage page, Frame? frame, ElementHandle? element)
    {
        Page = page;
        Frame = frame;
        Element = element;
    }

    public CdpPage Page { get; }

    public Frame? Frame { get; }

    /// <summary>The element that triggered the binding when the page passed one.</summary>
    public ElementHandle? Element { get; }

    public BrowserContext Context => Page.Context;
}

public sealed partial class CdpPage
{
    /// <summary>Exposes a function with a binding source argument.</summary>
    public ValueTask ExposeBindingAsync(
        string name,
        Func<BindingSource, string?, string?> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ExposeBindingAsync(
            name,
            (source, value) => new ValueTask<string?>(handler(source, value)),
            cancellationToken);
    }

    /// <summary>Exposes an asynchronous function with a binding source argument.</summary>
    public ValueTask ExposeBindingAsync(
        string name,
        Func<BindingSource, string?, ValueTask<string?>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ExposeFunctionAsync(
            name,
            value =>
            {
                Frame? frame = IsClosed ? null : MainFrame;
                return handler(new BindingSource(this, frame, element: null), value);
            },
            cancellationToken);
    }
}

public sealed partial class CdpPage
{
    /// <summary>Applies HTTP credentials to this page's network session.</summary>
    public async ValueTask SetHttpCredentialsAsync(
        HttpCredentials? credentials,
        CancellationToken cancellationToken = default)
    {
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        await _network.SetHttpCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
    }
}
