namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>Browser.grantPermissions</c>.</summary>
public readonly struct GrantPermissionsParams : ICdpCommandParams
{
    private readonly IReadOnlyList<string> _permissions;
    private readonly string? _origin;
    private readonly string? _browserContextId;

    public GrantPermissionsParams(
        IReadOnlyList<string> permissions,
        string? origin = null,
        string? browserContextId = null)
    {
        _permissions = permissions;
        _origin = origin;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("permissions"u8);
        writer.StringArray(_permissions);

        if (!string.IsNullOrEmpty(_origin))
        {
            writer.Name("origin"u8);
            writer.StringValue(_origin);
        }

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}

/// <summary>Parameters of <c>Browser.resetPermissions</c>.</summary>
public readonly struct ResetPermissionsParams : ICdpCommandParams
{
    private readonly string? _browserContextId;

    public ResetPermissionsParams(string? browserContextId = null) => _browserContextId = browserContextId;

    public void Write(ref CommandWriter writer)
    {
        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}

/// <summary>Parameters of <c>Browser.cancelDownload</c>.</summary>
public readonly struct CancelDownloadParams : ICdpCommandParams
{
    private readonly string _downloadKey;
    private readonly string? _browserContextId;

    public CancelDownloadParams(string downloadKey, string? browserContextId = null)
    {
        _downloadKey = downloadKey;
        _browserContextId = browserContextId;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("guid"u8);
        writer.StringValue(_downloadKey);

        if (!string.IsNullOrEmpty(_browserContextId))
        {
            writer.Name("browserContextId"u8);
            writer.StringValue(_browserContextId);
        }
    }
}
