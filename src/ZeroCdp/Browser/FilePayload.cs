namespace ZeroCdp.Browser;

/// <summary>An in-memory file supplied to a file input.</summary>
public sealed class FilePayload
{
    public string Name { get; init; } = string.Empty;

    public string MimeType { get; init; } = "application/octet-stream";

    public byte[] Buffer { get; init; } = Array.Empty<byte>();
}
