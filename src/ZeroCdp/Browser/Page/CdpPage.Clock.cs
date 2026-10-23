namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>Fake clock control for this page.</summary>
    public Clock Clock => _clock ??= new Clock(this);

    /// <summary>
    /// Replaces the page's clock init script and installs it into future documents through the
    /// existing HTML transformer. The script is opt-in and only present after Clock.InstallAsync.
    /// </summary>
    internal async ValueTask SetClockInitScriptAsync(
        string script,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(script);
        ObjectDisposedException.ThrowIf(IsClosed, this);

        lock (_initScriptGate)
        {
            _initScripts.RemoveAll(
                item => item.StartsWith(Clock.ScriptMarker, StringComparison.Ordinal));
            _initScripts.Add(script);
        }

        await EnableAsync(cancellationToken).ConfigureAwait(false);
        await _network.SetDocumentTransformerAsync(InjectInitScripts, cancellationToken)
            .ConfigureAwait(false);
    }
}
