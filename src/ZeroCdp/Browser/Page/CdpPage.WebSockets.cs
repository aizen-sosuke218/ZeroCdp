using System.Globalization;
using System.Text.Json;
using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    private const string WebSocketConsolePrefix = "__zc_ws:";

    private readonly Lock _webSocketRouteGate = new();
    private readonly SemaphoreSlim _webSocketInstallGate = new(1, 1);
    private readonly List<WebSocketRouteRegistration> _webSocketRoutes = new();
    private readonly Dictionary<long, WebSocketRoute> _webSocketRoutesById = new();
    private bool _webSocketInterceptorInstalled;

    /// <summary>
    /// Intercepts WebSockets created by this page whose URL matches a glob. Registering a route
    /// installs an opt-in main-world wrapper, because CDP cannot intercept WebSocket frames before
    /// they are sent. Without a route the wrapper is not installed and network observation keeps
    /// working through the regular Network domain events.
    /// </summary>
    public async ValueTask RouteWebSocketAsync(
        string urlPattern,
        Func<WebSocketRoute, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(urlPattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        lock (_webSocketRouteGate)
        {
            _webSocketRoutes.Add(new WebSocketRouteRegistration(urlPattern, handler));
        }

        await EnsureWebSocketInterceptorAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Intercepts WebSockets with a synchronous handler.</summary>
    public ValueTask RouteWebSocketAsync(
        string urlPattern,
        Action<WebSocketRoute> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RouteWebSocketAsync(
            urlPattern,
            route =>
            {
                handler(route);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Removes WebSocket routes for a URL glob, or every route when null.</summary>
    public ValueTask UnrouteWebSocketAsync(
        string? urlPattern = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

        lock (_webSocketRouteGate)
        {
            if (urlPattern is null)
            {
                _webSocketRoutes.Clear();
            }
            else
            {
                _webSocketRoutes.RemoveAll(route => route.Pattern.Equals(urlPattern, StringComparison.Ordinal));
            }
        }

        return ValueTask.CompletedTask;
    }

    internal async ValueTask EnsureWebSocketInterceptorAsync(CancellationToken cancellationToken)
    {
        await _webSocketInstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_webSocketInterceptorInstalled)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            await EnableAsync(cancellationToken).ConfigureAwait(false);

            // Runtime.consoleAPICalled is the only pre-navigation JS-to-managed channel that also
            // works in documents created later. Runtime.enable is opt-in and is never emitted by
            // ordinary page or stealth flows; routeWebSocket already makes the page observable.
            await Session.SendAsync<Unit>("Runtime.enable"u8, cancellationToken).ConfigureAwait(false);

            // The wrapper is inserted at the beginning of future HTML documents by the document
            // transformer, so it runs before page scripts and does not need a per-document binding.
            await AddInitScriptAsync(WebSocketInterceptorScript, cancellationToken).ConfigureAwait(false);

            EvaluationResult install = await _isolated
                .EvaluateMainWorldAsync(WebSocketInterceptorScript, cancellationToken)
                .ConfigureAwait(false);
            if (install.Threw)
            {
                throw new CdpException(
                    install.ExceptionSummary ?? "Installing the WebSocket interceptor failed in the page.");
            }

            _webSocketInterceptorInstalled = true;
        }
        finally
        {
            _webSocketInstallGate.Release();
        }
    }

    internal async ValueTask SendWebSocketControlAsync(
        long id,
        string command,
        string? payload,
        CancellationToken cancellationToken)
    {
        string expression = "globalThis.__zcWsControl("
            + id.ToString(CultureInfo.InvariantCulture)
            + "," + JsText.Quote(command)
            + "," + (payload ?? "null")
            + ")";
        try
        {
            await _isolated.EvaluateMainWorldAsync(expression, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CdpException or ObjectDisposedException or OperationCanceledException)
        {
            // The document may have navigated away while a frame was in flight.
        }
    }

    private bool TryResolveWebSocketRoute(
        string url,
        out Func<WebSocketRoute, ValueTask>? handler)
    {
        handler = null;
        lock (_webSocketRouteGate)
        {
            for (int i = _webSocketRoutes.Count - 1; i >= 0; i--)
            {
                WebSocketRouteRegistration route = _webSocketRoutes[i];
                if (UrlGlob.IsMatch(route.Pattern, url))
                {
                    handler = route.Handler;
                    return true;
                }
            }
        }

        return Context.TryResolveWebSocketRoute(url, out handler);
    }

    private void OnWebSocketConsoleMessage(string text)
    {
        if (!text.StartsWith(WebSocketConsolePrefix, StringComparison.Ordinal))
        {
            return;
        }

        string payload = text[WebSocketConsolePrefix.Length..];
        _ = Task.Run(() => HandleWebSocketBindingAsync(payload));
    }

    private async Task HandleWebSocketBindingAsync(string payload)
    {
        long id = 0;
        string kind;
        string? url = null;
        string data = string.Empty;
        bool binary = false;
        int closeCode = 1000;
        string? closeReason = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out JsonElement kindElement))
            {
                return;
            }

            kind = kindElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("id", out JsonElement idElement))
            {
                id = idElement.GetInt64();
            }

            if (root.TryGetProperty("url", out JsonElement urlElement))
            {
                url = urlElement.GetString();
            }

            if (root.TryGetProperty("data", out JsonElement dataElement))
            {
                data = dataElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("binary", out JsonElement binaryElement))
            {
                binary = binaryElement.GetBoolean();
            }

            if (root.TryGetProperty("code", out JsonElement codeElement))
            {
                closeCode = codeElement.GetInt32();
            }

            if (root.TryGetProperty("reason", out JsonElement reasonElement))
            {
                closeReason = reasonElement.GetString();
            }
        }
        catch (JsonException)
        {
            return;
        }

        switch (kind)
        {
            case "open":
                await HandleWebSocketOpenAsync(id, url ?? string.Empty).ConfigureAwait(false);
                break;
            case "client":
                if (TryGetWebSocketRoute(id, out WebSocketRoute? clientRoute))
                {
                    try
                    {
                        await clientRoute
                            .HandleClientFrameAsync(data, binary, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        await CloseFailedRouteAsync(id).ConfigureAwait(false);
                    }
                }
                break;
            case "server":
                if (TryGetWebSocketRoute(id, out WebSocketRoute? serverRoute))
                {
                    try
                    {
                        await serverRoute
                            .HandleServerFrameAsync(data, binary, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        await CloseFailedRouteAsync(id).ConfigureAwait(false);
                    }
                }
                break;
            case "clientClose":
            case "closed":
                if (RemoveWebSocketRoute(id, out WebSocketRoute? closedRoute))
                {
                    closedRoute.MarkClosed();
                }
                break;
        }

        _ = closeCode;
        _ = closeReason;
    }

    private async Task HandleWebSocketOpenAsync(long id, string url)
    {
        WebSocketRoute route = new(this, id, url);
        lock (_webSocketRouteGate)
        {
            _webSocketRoutesById[id] = route;
        }

        if (!TryResolveWebSocketRoute(url, out Func<WebSocketRoute, ValueTask>? handler)
            || handler is null)
        {
            try
            {
                await route.ConnectToServerAsync(CancellationToken.None).ConfigureAwait(false);
                await route.NotifyAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CdpException or ObjectDisposedException)
            {
                RemoveWebSocketRoute(id, out _);
            }

            return;
        }

        try
        {
            // Start the handler synchronously so its OnMessage registrations precede the page's
            // `open` event. A handler that awaits does not delay the page's open event.
            ValueTask handlerTask = handler(route);
            if (!route.IsClosed)
            {
                await route.NotifyAcceptedAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await handlerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            await route
                .CloseAsync(new WebSocketRouteCloseOptions { Code = 1011, Reason = "route handler failed" })
                .ConfigureAwait(false);
        }
    }

    private async Task CloseFailedRouteAsync(long id)
    {
        if (!RemoveWebSocketRoute(id, out WebSocketRoute route))
        {
            return;
        }

        try
        {
            await route
                .CloseAsync(new WebSocketRouteCloseOptions { Code = 1011, Reason = "route handler failed" })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The document may already be gone.
        }
    }

    private bool TryGetWebSocketRoute(long id, out WebSocketRoute route)
    {
        lock (_webSocketRouteGate)
        {
            return _webSocketRoutesById.TryGetValue(id, out route!);
        }
    }

    private bool RemoveWebSocketRoute(long id, out WebSocketRoute route)
    {
        lock (_webSocketRouteGate)
        {
            if (!_webSocketRoutesById.Remove(id, out route!))
            {
                route = null!;
                return false;
            }

            return true;
        }
    }

    internal void ClearWebSocketRoutes()
    {
        WebSocketRoute[] routes;
        lock (_webSocketRouteGate)
        {
            routes = _webSocketRoutesById.Values.ToArray();
            _webSocketRoutesById.Clear();
        }

        foreach (WebSocketRoute route in routes)
        {
            route.MarkClosed();
        }
    }

    private const string WebSocketInterceptorScript = """
        (() => {
          if (globalThis.__zcWsInstalled) return true;
          globalThis.__zcWsInstalled = true;

          const nativeLog = console.log.bind(console);
          const report = (message) => {
            try { nativeLog("__zc_ws:" + JSON.stringify(message)); } catch (error) { }
          };
          const NativeWebSocket = globalThis.WebSocket;
          const routes = new Map();
          let nextId = 0;

          const toBase64 = (bytes) => {
            let text = "";
            const chunk = 0x8000;
            for (let i = 0; i < bytes.length; i += chunk) {
              text += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
            }
            return btoa(text);
          };

          const fromBase64 = (text) => {
            const raw = atob(text);
            const bytes = new Uint8Array(raw.length);
            for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
            return bytes;
          };

          const describe = async (data) => {
            if (data instanceof ArrayBuffer) {
              return { data: toBase64(new Uint8Array(data)), binary: true };
            }
            if (ArrayBuffer.isView(data)) {
              return {
                data: toBase64(new Uint8Array(data.buffer, data.byteOffset, data.byteLength)),
                binary: true
              };
            }
            if (typeof Blob !== "undefined" && data instanceof Blob) {
              return { data: toBase64(new Uint8Array(await data.arrayBuffer())), binary: true };
            }
            return { data: String(data), binary: false };
          };

          const sendReport = (message) => report(message);

          class InterceptedWebSocket extends EventTarget {
            static CONNECTING = 0;
            static OPEN = 1;
            static CLOSING = 2;
            static CLOSED = 3;

            constructor(url, protocols) {
              super();
              this.url = String(url);
              this.protocol = "";
              this.extensions = "";
              this.bufferedAmount = 0;
              this.binaryType = "blob";
              this.readyState = 0;
              this.onopen = null;
              this.onmessage = null;
              this.onerror = null;
              this.onclose = null;
              this._id = ++nextId;
              this._url = this.url;
              this._protocols = protocols;
              this._native = null;
              this._pending = [];
              this._incoming = [];
              this._opened = false;
              this._accepted = false;
              this._connectRequested = false;
              this._closedReported = false;
              routes.set(this._id, this);
              sendReport({ kind: "open", id: this._id, url: this._url });
            }

            _emit(type, event) {
              const handler = this["on" + type];
              if (typeof handler === "function") {
                try { handler.call(this, event); } catch (error) { }
              }
              try { this.dispatchEvent(event); } catch (error) { }
            }

            _markOpen() {
              if (this._opened || this.readyState >= 2) return;
              if (!this._accepted) return;
              // For a proxied socket, do not tell the page it is open until the real server is
              // ready. Otherwise the page's synchronous `onopen` -> `send()` races the handshake.
              if (this._connectRequested && (!this._native || this._native.readyState !== 1)) return;
              this._opened = true;
              this.readyState = 1;
              this._emit("open", new Event("open"));
              const pending = this._pending;
              this._pending = [];
              for (const item of pending) this.send(item);
              const incoming = this._incoming;
              this._incoming = [];
              for (const description of incoming) this._emitMessage(description);
            }

            _accept() {
              this._accepted = true;
              this._markOpen();
            }

            _connect() {
              this._connectRequested = true;
              if (this._native || this.readyState >= 2) return;
              try {
                const native = this._protocols === undefined
                  ? new NativeWebSocket(this._url)
                  : new NativeWebSocket(this._url, this._protocols);
                this._native = native;
                native.binaryType = "arraybuffer";
                native.addEventListener("open", () => {
                  this.protocol = native.protocol;
                  this.extensions = native.extensions;
                  this._markOpen();
                });
                native.addEventListener("message", async (event) => {
                  const description = await describe(event.data);
                  sendReport(Object.assign({ kind: "server", id: this._id }, description));
                });
                native.addEventListener("error", () => {
                  this._emit("error", new Event("error"));
                });
                native.addEventListener("close", (event) => {
                  this._finishClose(event.code, event.reason, event.wasClean);
                });
              } catch (error) {
                this._finishClose(1006, String(error), false);
              }
            }

            _sendToNative(data) {
              if (!this._native || this._native.readyState !== 1) return;
              this._native.send(data);
            }

            _deliver(description) {
              if (this.readyState === 0) {
                this._incoming.push(description);
                return;
              }
              if (this.readyState !== 1) return;
              this._emitMessage(description);
            }

            _emitMessage(description) {
              let data;
              if (description && description.binary) {
                const bytes = fromBase64(description.data);
                if (this.binaryType === "blob") {
                  data = new Blob([bytes]);
                } else {
                  data = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
                }
              } else {
                data = description ? String(description.data) : "";
              }
              this._emit("message", new MessageEvent("message", { data }));
            }

            _finishClose(code, reason, wasClean) {
              if (this.readyState === 3) return;
              this.readyState = 3;
              routes.delete(this._id);
              this._emit("close", new CloseEvent("close", {
                code: code,
                reason: reason || "",
                wasClean: wasClean
              }));
              if (!this._closedReported) {
                this._closedReported = true;
                sendReport({ kind: "closed", id: this._id });
              }
            }

            send(data) {
              if (this.readyState === 0) {
                this._pending.push(data);
                return;
              }
              if (this.readyState !== 1) {
                throw new DOMException("WebSocket is not open", "InvalidStateError");
              }
              describe(data).then(
                (description) => sendReport(Object.assign({ kind: "client", id: this._id }, description)),
                () => { });
            }

            close(code, reason) {
              if (this.readyState >= 2) return;
              const closeCode = code === undefined ? 1000 : code;
              const closeReason = reason === undefined ? "" : String(reason);
              this.readyState = 2;
              sendReport({
                kind: "clientClose",
                id: this._id,
                code: closeCode,
                reason: closeReason
              });
              if (this._native) {
                try { this._native.close(closeCode, closeReason); } catch (error) { }
              } else {
                this._finishClose(closeCode, closeReason, true);
              }
            }
          }

          globalThis.__zcWsControl = (id, command, payload) => {
            const route = routes.get(Number(id));
            if (!route) return;
            if (command === "accept") {
              route._accept();
            } else if (command === "connect") {
              route._connect();
            } else if (command === "deliver") {
              route._deliver(payload);
            } else if (command === "toServer") {
              route._sendToNative(payload && payload.binary
                ? fromBase64(payload.data).buffer
                : (payload ? String(payload.data) : ""));
            } else if (command === "close") {
              if (route._native) {
                try {
                  route._native.close(
                    payload ? payload.code : 1000,
                    payload ? payload.reason : "");
                } catch (error) { }
              }
              route._finishClose(
                payload ? payload.code : 1000,
                payload ? payload.reason : "",
                true);
            }
            return true;
          };

          globalThis.WebSocket = InterceptedWebSocket;
          return true;
        })()
        """;
}

internal sealed class WebSocketRouteRegistration
{
    public WebSocketRouteRegistration(string pattern, Func<WebSocketRoute, ValueTask> handler)
    {
        Pattern = pattern;
        Handler = handler;
    }

    public string Pattern { get; }

    public Func<WebSocketRoute, ValueTask> Handler { get; }
}
