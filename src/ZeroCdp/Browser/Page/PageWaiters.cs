namespace ZeroCdp.Browser;

internal sealed class PopupWaiter
{
    public PopupWaiter(Func<CdpPage, bool>? predicate)
    {
        Predicate = predicate;
    }

    public Func<CdpPage, bool>? Predicate { get; }

    public TaskCompletionSource<CdpPage> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class DownloadWaiter
{
    public DownloadWaiter(Func<Download, bool>? predicate)
    {
        Predicate = predicate;
    }

    public Func<Download, bool>? Predicate { get; }

    public TaskCompletionSource<Download> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class FileChooserWaiter
{
    public FileChooserWaiter(Func<FileChooser, bool>? predicate)
    {
        Predicate = predicate;
    }

    public Func<FileChooser, bool>? Predicate { get; }

    public TaskCompletionSource<FileChooser> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
