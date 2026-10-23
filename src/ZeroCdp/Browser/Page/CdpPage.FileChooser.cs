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
    internal async ValueTask SetFileInputPayloadsAsync(
        string frameId,
        long backendNodeId,
        IReadOnlyList<FilePayload> files,
        CancellationToken cancellationToken)
    {
        FrameContext context = GetFrameContext(frameId);
        await context.World.EvaluateAsync("undefined", cancellationToken).ConfigureAwait(false);
        int executionContextId = context.World.ExecutionContextId;

        ResolveNodeResult resolved = await context.Session
            .SendAsync<ResolveNodeResult, ResolveNodeParams>(
                "DOM.resolveNode"u8,
                new ResolveNodeParams(backendNodeId, useBackendNodeId: true, executionContextId),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(resolved.ObjectId))
        {
            throw new CdpException("The file input could not be resolved to a JavaScript object.");
        }

        System.Text.StringBuilder payloads = new(files.Count * 64 + 2);
        payloads.Append('[');
        for (int i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                payloads.Append(',');
            }

            FilePayload file = files[i];
            payloads.Append("{\"name\":").Append(JsText.Quote(file.Name))
                .Append(",\"mimeType\":").Append(JsText.Quote(file.MimeType))
                .Append(",\"data\":").Append(JsText.Quote(Convert.ToBase64String(file.Buffer)))
                .Append('}');
        }

        payloads.Append(']');
        string arguments = "[{\"value\":" + payloads + "}]";

        const string Function = """
            function(payloads) {
              const transfer = new DataTransfer();
              for (const payload of payloads) {
                const binary = atob(payload.data);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
                transfer.items.add(new File([bytes], payload.name, { type: payload.mimeType }));
              }
              this.files = transfer.files;
              this.dispatchEvent(new Event('input', { bubbles: true }));
              this.dispatchEvent(new Event('change', { bubbles: true }));
              return Array.from(this.files).map(file => file.name);
            }
            """;

        EvaluationResult result = await context.World
            .CallFunctionOnObjectAsync(resolved.ObjectId, Function, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Setting in-memory files threw in the page.");
        }
    }

    /// <summary>
    /// Raised on the reader thread after a file chooser was intercepted. Interception must first
    /// be enabled with <see cref="EnableFileChooserInterceptionAsync"/>; waiters enable it
    /// automatically.
    /// </summary>
    public event Action<FileChooser>? FileChooserOpening
    {
        add
        {
            lock (_fileChooserGate)
            {
                _fileChooserHandlers += value;
            }
        }
        remove
        {
            lock (_fileChooserGate)
            {
                _fileChooserHandlers -= value;
            }
        }
    }

    /// <summary>
    /// Enables or disables interception of native file dialogs. While enabled, Chromium emits
    /// <c>Page.fileChooserOpened</c> instead of blocking on a dialog. Leave interception on only
    /// when a handler is ready to accept or cancel each chooser.
    /// </summary>
    public async ValueTask EnableFileChooserInterceptionAsync(
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        await Session
            .SendAsync<Unit, SetInterceptFileChooserDialogParams>(
                "Page.setInterceptFileChooserDialog"u8,
                new SetInterceptFileChooserDialogParams(enabled),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the next intercepted file chooser. Call this before the click that opens it, or
    /// use <see cref="RunAndWaitForFileChooserAsync"/>.
    /// </summary>
    public ValueTask<FileChooser> WaitForFileChooserAsync(
        Func<FileChooser, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForFileChooserSetupAsync(action: null, predicate, timeout, cancellationToken);

    /// <summary>Runs an action and returns the file chooser it opens.</summary>
    public ValueTask<FileChooser> RunAndWaitForFileChooserAsync(
        Func<ValueTask> action,
        Func<FileChooser, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WaitForFileChooserSetupAsync(action, predicate, timeout, cancellationToken);
    }

    private async ValueTask<FileChooser> WaitForFileChooserSetupAsync(
        Func<ValueTask>? action,
        Func<FileChooser, bool>? predicate,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        await EnableFileChooserInterceptionAsync(enabled: true, cancellationToken).ConfigureAwait(false);
        FileChooserWaiter waiter = new(predicate);
        lock (_fileChooserGate)
        {
            _fileChooserWaiters.Add(waiter);
        }

        try
        {
            if (action is not null)
            {
                await action().ConfigureAwait(false);
            }

            TimeSpan effective = timeout ?? DefaultTimeout;
            try
            {
                return await waiter.Completion.Task
                    .WaitAsync(effective, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new CdpTimeoutException(
                    $"No file chooser opened by page '{TargetId}' within {effective.TotalSeconds:0.###}s.");
            }
        }
        finally
        {
            lock (_fileChooserGate)
            {
                _fileChooserWaiters.Remove(waiter);
            }

            try
            {
                await EnableFileChooserInterceptionAsync(enabled: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // The page may have navigated or closed while the chooser was open.
            }
        }
    }

    internal async ValueTask SetFileInputFilesByBackendNodeAsync(
        long backendNodeId,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
        => await Session
            .SendAsync<Unit, SetFileInputFilesParams>(
                "DOM.setFileInputFiles"u8,
                new SetFileInputFilesParams(files, backendNodeId),
                cancellationToken)
            .ConfigureAwait(false);

    private void OnFileChooserOpened(FileChooserInfo info)
    {
        FileChooser chooser = new(this, info.FrameId, info.BackendNodeId, info.IsMultiple);

        Action<FileChooser>? handlers;
        FileChooserWaiter[] waiters;
        lock (_fileChooserGate)
        {
            handlers = _fileChooserHandlers;
            waiters = _fileChooserWaiters.ToArray();
        }

        foreach (FileChooserWaiter waiter in waiters)
        {
            try
            {
                if (waiter.Predicate is null || waiter.Predicate(chooser))
                {
                    waiter.Completion.TrySetResult(chooser);
                }
            }
            catch (Exception ex) when (ex is CdpException or InvalidOperationException)
            {
                waiter.Completion.TrySetException(ex);
            }
        }

        handlers?.Invoke(chooser);
    }

    /// <summary>
    /// Raised on the reader thread for every request observed by the Network domain, after
    /// <see cref="EnableNetworkAsync"/> or a network waiter has turned observation on.
    /// </summary>
}
