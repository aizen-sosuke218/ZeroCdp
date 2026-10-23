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
    /// Polls an expression until it returns a truthy value. Evaluation is isolated by default;
    /// pass <paramref name="isolatedContext"/> false for an explicit main-world check.
    /// </summary>
    public async ValueTask<EvaluationResult> WaitForFunctionAsync(
        string expression,
        TimeSpan? timeout = null,
        bool isolatedContext = true,
        CancellationToken cancellationToken = default)
        => await WaitForFunctionCoreAsync(
                expression,
                timeout ?? DefaultTimeout,
                TimeSpan.FromMilliseconds(50),
                isolatedContext,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Playwright-style wait-for-function overload using an options object.</summary>
    public ValueTask<EvaluationResult> WaitForFunctionAsync(
        string expression,
        PageWaitForFunctionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan polling = options.PollingInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(50)
            : options.PollingInterval;
        return WaitForFunctionCoreAsync(
            expression,
            options.Timeout ?? DefaultTimeout,
            polling,
            isolatedContext: true,
            cancellationToken: cancellationToken);
    }

    private async ValueTask<EvaluationResult> WaitForFunctionCoreAsync(
        string expression,
        TimeSpan effectiveTimeout,
        TimeSpan pollingInterval,
        bool isolatedContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        long deadline = Environment.TickCount64 + (long)effectiveTimeout.TotalMilliseconds;

        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

            CdpException? expressionError = null;
            try
            {
                EvaluationResult result = await EvaluateAsync(
                        expression,
                        isolatedContext,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Threw)
                {
                    expressionError = new CdpException(
                        result.ExceptionSummary ?? "The wait expression threw in the page.");
                }
                else if (IsTruthy(result.Value))
                {
                    return result;
                }
            }
            catch (CdpException)
            {
                // A navigation can invalidate the execution context; retry with a fresh world.
            }

            if (expressionError is not null)
            {
                throw expressionError;
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new CdpTimeoutException(
                    $"Expression '{expression}' did not become truthy within {effectiveTimeout.TotalSeconds:0.##}s.");
            }

            await Task.Delay(pollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for a selector to reach a state and returns its locator.</summary>
    public async ValueTask<Locator> WaitForSelectorAsync(
        string selector,
        WaitForState state = WaitForState.Visible,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        Locator locator = Locator(selector, timeout: timeout);
        await locator.WaitForAsync(state, timeout, cancellationToken).ConfigureAwait(false);
        return locator;
    }

    /// <summary>Playwright-style wait-for-selector overload using an options object.</summary>
    public ValueTask<Locator> WaitForSelectorAsync(
        string selector,
        PageWaitForSelectorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WaitForSelectorAsync(
            selector,
            options.State.ToWaitForState(),
            options.Timeout,
            cancellationToken);
    }

    /// <summary>Delays for a fixed interval; useful for tests that intentionally wait.</summary>
    public ValueTask WaitForTimeoutAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        return new ValueTask(Task.Delay(timeout, cancellationToken));
    }

    internal static bool IsTruthy(RemoteObject value)
    {
        if (!value.HasValue || value.IsNull || value.IsUndefined)
        {
            return false;
        }

        return value.Type switch
        {
            "boolean" => value.TryGetBoolean(out bool boolean) && boolean,
            "number" => value.TryGetDouble(out double number)
                && number != 0
                && !double.IsNaN(number),
            "string" => !string.IsNullOrEmpty(value.StringValue),
            _ => true,
        };
    }

    /// <summary>
    /// Evaluates JavaScript in this page's isolated world and returns its value.
    /// </summary>
    /// <remarks>
    /// The Runtime domain is not enabled. The first call creates the isolated world through
    /// <c>Page.createIsolatedWorld</c>; later calls reuse its context until a navigation commits.
    /// A JavaScript exception is reported through <see cref="EvaluationResult.Threw"/> instead of
    /// being thrown as a protocol error.
    /// </remarks>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        return await _isolated.EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates JavaScript in the isolated world by default, or in the main world when
    /// <paramref name="isolatedContext"/> is false. Main-world execution is a Patchright-compatible
    /// opt-in: page scripts can observe any globals or side effects it creates.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateAsync(
        string expression,
        bool isolatedContext,
        CancellationToken cancellationToken = default)
    {
        if (isolatedContext)
        {
            return await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        return await _isolated.EvaluateMainWorldAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exposes a JavaScript function that calls back into managed code.
    /// </summary>
    /// <remarks>
    /// The page-visible function accepts one value (converted to a string) and returns a promise
    /// that resolves with the handler's result. The binding is created with
    /// <c>Runtime.addBinding</c>, which reports <c>Runtime.bindingCalled</c> without enabling the
    /// Runtime domain. The injected wrapper exists only in the current document: call this again
    /// after a navigation, or pair it with <see cref="AddInitScriptAsync"/>.
    /// </remarks>
    public ValueTask ExposeFunctionAsync(
        string name,
        Func<string?, string?> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ExposeFunctionAsync(
            name,
            value => new ValueTask<string?>(handler(value)),
            cancellationToken);
    }

    /// <summary>Exposes an asynchronous JavaScript function that calls back into managed code.</summary>
    public async ValueTask ExposeFunctionAsync(
        string name,
        Func<string?, ValueTask<string?>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);

        string bindingName = "__zc_" + WorldNameFactory.Create()[..20];
        string responseName = bindingName + "r";

        lock (_bindingGate)
        {
            _bindings[bindingName] = handler;
            _bindingResponses[bindingName] = responseName;
        }

        await Session
            .SendAsync<Unit, AddBindingParams>(
                "Runtime.addBinding"u8,
                new AddBindingParams(bindingName),
                cancellationToken)
            .ConfigureAwait(false);

        string script = """
            (() => {
              const send = globalThis[%REQ%];
              const pending = Object.create(null);
              let nextId = 1;
              globalThis[%RES%] = (payload) => {
                let message;
                try { message = JSON.parse(payload); } catch (error) { return; }
                const entry = pending[message.id];
                if (!entry) return;
                delete pending[message.id];
                if (message.error) entry.reject(new Error(message.error));
                else entry.resolve(message.value);
              };
              globalThis[%NAME%] = (value) => new Promise((resolve, reject) => {
                const id = nextId++;
                pending[id] = { resolve, reject };
                send(JSON.stringify({ id: id, value: value === undefined ? null : String(value) }));
              });
              return true;
            })()
            """
            .Replace("%REQ%", JsText.Quote(bindingName), StringComparison.Ordinal)
            .Replace("%RES%", JsText.Quote(responseName), StringComparison.Ordinal)
            .Replace("%NAME%", JsText.Quote(name), StringComparison.Ordinal);

        // The function has to live in the main world because page scripts call it. This is an
        // explicit opt-in surface, not something an ordinary page load pays for.
        EvaluationResult install = await _isolated
            .EvaluateMainWorldAsync(script, cancellationToken)
            .ConfigureAwait(false);

        if (install.Threw)
        {
            throw new CdpException(install.ExceptionSummary ?? "Installing the exposed function failed in the page.");
        }
    }

    private void OnBindingCalled(string bindingName, string? payload)
    {
        Func<string?, ValueTask<string?>>? handler;
        string? responseName;
        lock (_bindingGate)
        {
            _bindings.TryGetValue(bindingName, out handler);
            _bindingResponses.TryGetValue(bindingName, out responseName);
        }

        if (handler is null || responseName is null)
        {
            return;
        }

        _ = Task.Run(() => InvokeBindingAsync(handler, responseName, payload));
    }

    private async Task InvokeBindingAsync(
        Func<string?, ValueTask<string?>> handler,
        string responseName,
        string? payload)
    {
        long id = 0;
        string? value = null;
        try
        {
            Json.JsonCursor cursor = new(System.Text.Encoding.UTF8.GetBytes(payload ?? "{}"));
            if (cursor.TryEnterObject())
            {
                while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
                {
                    if (property.SequenceEqual("id"u8))
                    {
                        cursor.TryGetInt64(out id);
                    }
                    else if (property.SequenceEqual("value"u8))
                    {
                        if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out bool escaped))
                        {
                            if (!escaped)
                            {
                                value = System.Text.Encoding.UTF8.GetString(raw);
                            }
                            else
                            {
                                byte[] buffer = new byte[raw.Length];
                                value = Json.JsonUtf8.TryUnescape(raw, buffer, out int written)
                                    ? System.Text.Encoding.UTF8.GetString(buffer, 0, written)
                                    : string.Empty;
                            }
                        }
                        else
                        {
                            cursor.SkipValue();
                        }
                    }
                    else if (!cursor.SkipValue())
                    {
                        break;
                    }
                }
            }

            string? result = await handler(value).ConfigureAwait(false);
            string message = "{\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"value\":" + JsText.Quote(result ?? string.Empty) + "}";
            await DeliverBindingResponseAsync(responseName, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = "{\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"error\":" + JsText.Quote(ex.Message) + "}";
            try
            {
                await DeliverBindingResponseAsync(responseName, message).ConfigureAwait(false);
            }
            catch (Exception deliveryFailure) when (deliveryFailure is CdpException or ObjectDisposedException)
            {
                // The page or browser went away while the handler was running.
            }
        }
    }

    private async ValueTask DeliverBindingResponseAsync(string responseName, string message)
    {
        string expression = "globalThis[" + JsText.Quote(responseName) + "](" + JsText.Quote(message) + ")";
        await _isolated.EvaluateMainWorldAsync(expression, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a script before the page's own scripts on every subsequent document.
    /// </summary>
    /// <remarks>
    /// Patchright's approach is followed: the script is inserted into the HTML response through
    /// request interception instead of using <c>Page.addScriptToEvaluateOnNewDocument</c>, which is
    /// a detectable command. The trade-offs are the same ones Patchright documents: a strict CSP
    /// can block the inline script, redirects that never deliver HTML are not rewritten, and the
    /// rewritten response can be observed through timing.
    /// </remarks>
    public async ValueTask AddInitScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        lock (_initScriptGate)
        {
            _initScripts.Add(script);
        }

        await EnableAsync(cancellationToken).ConfigureAwait(false);
        await _network.SetDocumentTransformerAsync(InjectInitScripts, cancellationToken).ConfigureAwait(false);
    }

    private string InjectInitScripts(string html)
    {
        StringBuilder injection = new();
        lock (_initScriptGate)
        {
            foreach (string script in _initScripts)
            {
                injection.Append("<script>")
                    .Append(script.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase))
                    .Append("</script>");
            }
        }

        if (injection.Length == 0)
        {
            return html;
        }

        return html.Insert(FindInjectionPoint(html), injection.ToString());
    }

    private static int FindInjectionPoint(string html)
    {
        int head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            int close = html.IndexOf('>', head);
            if (close >= 0)
            {
                return close + 1;
            }
        }

        int htmlTag = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlTag >= 0)
        {
            int close = html.IndexOf('>', htmlTag);
            if (close >= 0)
            {
                return close + 1;
            }
        }

        // Never insert before the doctype: that would put the document in quirks mode.
        int doctype = html.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
        if (doctype >= 0)
        {
            int close = html.IndexOf('>', doctype);
            if (close >= 0)
            {
                return close + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Calls a function inside the isolated world. Arguments are supplied as a raw CDP
    /// <c>CallArgument</c> JSON array, for example <c>[{"value":"abc"}]</c>.
    /// </summary>
    public async ValueTask<EvaluationResult> EvaluateFunctionAsync(
        string functionDeclaration,
        string argumentsJson = "[]",
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        return await _isolated.CallFunctionAsync(functionDeclaration, argumentsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serialized markup of the document element.
    /// </summary>
    /// <remarks>
    /// This is the DOM's current state, not the original response body, and it starts at
    /// <c>&lt;html&gt;</c> — the doctype is not included.
    /// </remarks>
}
