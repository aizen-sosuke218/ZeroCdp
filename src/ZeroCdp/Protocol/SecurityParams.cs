using ZeroCdp.Json;

namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Security.setIgnoreCertificateErrors</c>.</summary>
public readonly struct SetIgnoreCertificateErrorsParams : ICdpCommandParams
{
    private readonly bool _ignore;

    public SetIgnoreCertificateErrorsParams(bool ignore) => _ignore = ignore;

    public void Write(ref CommandWriter writer)
    {
        writer.Name("ignore"u8);
        writer.BoolValue(_ignore);
    }
}
