using System.Text;
using ZeroCdp.Dom;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;
using ZeroCdp.Selectors;
using ZeroCdp.Stealth;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Dismiss dialogs automatically when no <see cref="DialogOpening"/> subscriber is attached.
    /// True by default, matching the "inaction dismisses" behaviour of other automation clients.
    /// </summary>
    public bool AutoDismissDialogs { get; set; } = true;

    /// <summary>True while a JavaScript dialog is blocking this page.</summary>
    public bool IsDialogOpen => Volatile.Read(ref _dialogOpen) != 0;

    /// <summary>The dialog currently blocking the page, if any.</summary>
    public DialogInfo CurrentDialog => _currentDialog;

    /// <summary>
    /// Raised on the reader thread when the page opens a dialog. With a subscriber attached the
    /// dialog stays open until <see cref="AcceptDialogAsync"/> or <see cref="DismissDialogAsync"/>
    /// is called; without one it is dismissed when <see cref="AutoDismissDialogs"/> is true.
    /// </summary>
    public event Action<DialogInfo>? DialogOpening
    {
        add
        {
            lock (_dialogGate)
            {
                _dialogHandlers += value;
            }
        }
        remove
        {
            lock (_dialogGate)
            {
                _dialogHandlers -= value;
            }
        }
    }

    /// <summary>Accepts the dialog currently blocking the page.</summary>
    /// <param name="promptText">Text to return for a prompt; ignored for other dialog types.</param>
    /// <returns>False when no dialog was open.</returns>
    public ValueTask<bool> AcceptDialogAsync(
        string? promptText = null,
        CancellationToken cancellationToken = default)
        => HandleDialogAsync(accept: true, promptText, cancellationToken);

    /// <summary>Dismisses the dialog currently blocking the page.</summary>
    /// <returns>False when no dialog was open.</returns>
    public ValueTask<bool> DismissDialogAsync(CancellationToken cancellationToken = default)
        => HandleDialogAsync(accept: false, promptText: null, cancellationToken);

    /// <summary>Captures a PNG screenshot of the viewport.</summary>

    private void OnDialogOpening(DialogInfo dialog)
    {
        _currentDialog = dialog;
        Volatile.Write(ref _dialogOpen, 1);

        Action<DialogInfo>? handlers;
        lock (_dialogGate)
        {
            handlers = _dialogHandlers;
        }

        if (handlers is not null)
        {
            handlers(dialog);
            return;
        }

        if (AutoDismissDialogs)
        {
            _ = DismissDialogQuietlyAsync();
        }
    }

    private void OnDialogClosed()
    {
        Volatile.Write(ref _dialogOpen, 0);
        _currentDialog = default;
    }

    private async ValueTask<bool> HandleDialogAsync(
        bool accept,
        string? promptText,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _dialogOpen) == 0)
        {
            return false;
        }

        try
        {
            await Session
                .SendAsync<Unit, HandleJavaScriptDialogParams>(
                    "Page.handleJavaScriptDialog"u8,
                    new HandleJavaScriptDialogParams(accept, promptText),
                    cancellationToken)
                .ConfigureAwait(false);

            Volatile.Write(ref _dialogOpen, 0);
            return true;
        }
        catch (CdpCommandException ex) when (ex.Message.Contains("No dialog", StringComparison.OrdinalIgnoreCase))
        {
            // The dialog was already accepted or dismissed by another caller.
            Volatile.Write(ref _dialogOpen, 0);
            return false;
        }
    }

    private async Task DismissDialogQuietlyAsync()
    {
        try
        {
            await DismissDialogAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or OperationCanceledException or ObjectDisposedException)
        {
            // The page or browser went away while the dialog was open.
        }
    }
}
