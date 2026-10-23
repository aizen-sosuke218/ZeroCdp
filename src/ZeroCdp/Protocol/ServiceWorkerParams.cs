using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Network.setBypassServiceWorker</c>.</summary>
public readonly struct SetBypassServiceWorkerParams : ICdpCommandParams
{
    private readonly bool _bypass;

    public SetBypassServiceWorkerParams(bool bypass) => _bypass = bypass;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("bypass"u8);
        writer.BoolValue(_bypass);
    }
}
