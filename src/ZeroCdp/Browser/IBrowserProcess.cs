namespace ZeroCdp.Browser;

/// <summary>A launched browser process, independent of how it was spawned.</summary>
internal interface IBrowserProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    void Kill();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Wraps a <see cref="System.Diagnostics.Process"/> started in loopback port mode.</summary>
internal sealed class ManagedBrowserProcess : IBrowserProcess
{
    private readonly System.Diagnostics.Process _process;

    public ManagedBrowserProcess(System.Diagnostics.Process process) => _process = process;

    public int Id
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public void Dispose() => _process.Dispose();
}
