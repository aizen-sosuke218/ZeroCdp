using System.Net;
using System.Net.Sockets;
using System.Text;
using ZeroCdp.Browser;

// A realistic, self-contained ZeroCDP example. It starts its own HTTP server on a random port,
// launches a private headless browser, exercises a small storefront with Playwright-style options,
// and writes a screenshot. It is safe to run while the test suite is running: no fixed port,
// no shared profile and no external network access.

string outputPath = Path.Combine(
    Path.GetTempPath(),
    "zerocdp-realworld-" + Guid.NewGuid().ToString("N") + ".png");

await using HttpServer server = new();
await using LaunchedBrowser launched = await BrowserLauncher.LaunchAsync(new LaunchOptions
{
    Headless = true,
});

await using BrowserContext context = await launched.Browser.NewContextAsync(new BrowserContextOptions
{
    Viewport = new ViewportSize(1100, 800),
});

CdpPage page = await context.NewPageAsync();
page.SetDefaultTimeout(10_000);

Console.WriteLine($"server    : {server.Url("/")}");
Console.WriteLine($"browser   : {launched.Connection.TransportDescription}");

await page.GotoAsync(server.Url("/"), new PageGotoOptions
{
    WaitUntil = WaitUntilState.Load,
});

// Search by accessible role and name, then wait for the filtered product to be visible.
await page
    .GetByRole(AriaRole.Searchbox, new RoleOptions { Name = "Search products" })
    .FillAsync("keyboard", new LocatorFillOptions { Timeout = TimeSpan.FromSeconds(5) });

Locator keyboard = page.GetByRole(
    AriaRole.Heading,
    new RoleOptions { Name = "Mechanical Keyboard" });
await keyboard.WaitForAsync(timeout: TimeSpan.FromSeconds(5));
Assert(await keyboard.TextAsync() == "Mechanical Keyboard", "search did not find the keyboard");

// Add it to the cart with a Playwright-style click options object.
await page
    .GetByRole(AriaRole.Button, new RoleOptions { Name = "Add Mechanical Keyboard to cart" })
    .ClickAsync(new LocatorClickOptions { Timeout = TimeSpan.FromSeconds(5) });

Assert(await page.Locator("#cart-count").TextAsync() == "1", "cart count was not updated");
Assert(await page.Locator("#cart-total").TextAsync() == "$89.00", "cart total was not updated");

// Fill the checkout form using label-based locators.
await page.GetByLabel("Full name").FillAsync(
    "Ada Lovelace",
    new LocatorFillOptions { Timeout = TimeSpan.FromSeconds(5) });
await page.GetByLabel("Email").FillAsync(
    "ada@example.test",
    new LocatorFillOptions { Timeout = TimeSpan.FromSeconds(5) });

// The submit navigates to /receipt. Start the URL waiter before clicking so the navigation
// cannot be missed, which is the same pattern Playwright recommends.
ValueTask<string> navigation = page.WaitForURLAsync(
    server.Url("/receipt") + "*",
    TimeSpan.FromSeconds(10));
ValueTask click = page
    .GetByRole(AriaRole.Button, new RoleOptions { Name = "Place order" })
    .ClickAsync(new LocatorClickOptions { Timeout = TimeSpan.FromSeconds(10) });

await Task.WhenAll(click.AsTask(), navigation.AsTask());

string receipt = await page
    .GetByRole(AriaRole.Heading, new RoleOptions { Name = "Order confirmed" })
    .TextAsync();
Assert(receipt == "Order confirmed", "receipt heading was not found");
Assert((await page.UrlAsync()).Contains("/receipt", StringComparison.Ordinal), "receipt URL was not reached");

await page.ScreenshotAsync(new PageScreenshotOptions
{
    Path = outputPath,
    FullPage = true,
    Type = ScreenshotType.Png,
});

Console.WriteLine("flow      : search -> add to cart -> checkout -> receipt");
Console.WriteLine($"screenshot: {outputPath}");
Console.WriteLine("sample    : PASS");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("Real-world sample failed: " + message);
    }
}

internal sealed class HttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public HttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[]? header = await ReadHeaderAsync(stream).ConfigureAwait(false);
                if (header is null)
                {
                    return;
                }

                string requestLine = Encoding.UTF8.GetString(header).Split("\r\n", 2, StringSplitOptions.None)[0];
                string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                string path = parts.Length > 1 ? parts[1] : "/";
                int query = path.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                {
                    path = path[..query];
                }

                (int status, string contentType, string body) = path switch
                {
                    "/" => (200, "text/html; charset=utf-8", StorefrontHtml),
                    "/receipt" => (200, "text/html; charset=utf-8", ReceiptHtml),
                    "/favicon.ico" => (204, "image/x-icon", string.Empty),
                    _ => (404, "text/plain; charset=utf-8", "not found"),
                };

                await WriteResponseAsync(stream, status, contentType, body).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // The browser closed the connection or the sample is shutting down.
            }
        }
    }

    private static async Task<byte[]?> ReadHeaderAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];
        using MemoryStream accumulated = new();
        while (accumulated.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            accumulated.Write(buffer, 0, read);
            byte[] current = accumulated.GetBuffer();
            int length = (int)accumulated.Length;
            for (int i = 3; i < length; i++)
            {
                if (current[i - 3] == '\r' && current[i - 2] == '\n'
                    && current[i - 1] == '\r' && current[i] == '\n')
                {
                    return current.AsSpan(0, i + 1).ToArray();
                }
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            _ => "Not Found",
        };

        string headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(headers)).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private const string StorefrontHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Acme Supply</title>
          <style>
            body { font: 16px/1.5 system-ui, sans-serif; margin: 0; color: #17202a; background: #f6f7f9; }
            header { display: flex; justify-content: space-between; align-items: center; padding: 18px 32px; background: #17202a; color: white; }
            header h1 { margin: 0; font-size: 22px; }
            main { max-width: 880px; margin: 28px auto; padding: 0 24px; }
            label { display: block; font-weight: 600; margin: 16px 0 6px; }
            input { width: 100%; box-sizing: border-box; padding: 10px 12px; border: 1px solid #c7cdd4; border-radius: 6px; font: inherit; }
            #products { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); padding: 0; list-style: none; }
            .product { background: white; border: 1px solid #dde2e8; border-radius: 10px; padding: 18px; }
            .product h2 { margin: 0 0 4px; font-size: 18px; }
            .product p { color: #52606d; margin: 0 0 14px; }
            button { background: #0b6bcb; color: white; border: 0; border-radius: 6px; padding: 9px 14px; font: inherit; cursor: pointer; }
            button:hover { background: #0956a3; }
            #cart { margin-top: 24px; padding: 18px; background: white; border: 1px solid #dde2e8; border-radius: 10px; }
            #cart h2 { margin-top: 0; }
            #confirmation { color: #0a7a3d; font-weight: 700; }
          </style>
        </head>
        <body>
          <header>
            <h1>Acme Supply</h1>
            <span>Cart: <strong id="cart-count">0</strong></span>
          </header>
          <main>
            <label for="search">Search products</label>
            <input id="search" type="search" placeholder="Search products">
            <ul id="products">
              <li class="product" data-name="Mechanical Keyboard" data-price="89">
                <h2>Mechanical Keyboard</h2><p>$89.00</p>
                <button class="add" aria-label="Add Mechanical Keyboard to cart">Add to cart</button>
              </li>
              <li class="product" data-name="Studio Monitor" data-price="249">
                <h2>Studio Monitor</h2><p>$249.00</p>
                <button class="add" aria-label="Add Studio Monitor to cart">Add to cart</button>
              </li>
              <li class="product" data-name="USB-C Dock" data-price="129">
                <h2>USB-C Dock</h2><p>$129.00</p>
                <button class="add" aria-label="Add USB-C Dock to cart">Add to cart</button>
              </li>
            </ul>
            <section id="cart" hidden>
              <h2>Your cart</h2>
              <p>Total: <strong id="cart-total">$0.00</strong></p>
              <form id="checkout" action="/receipt" method="get">
                <label for="name">Full name</label>
                <input id="name" name="name" autocomplete="name" required>
                <label for="email">Email</label>
                <input id="email" name="email" type="email" autocomplete="email" required>
                <p><button type="submit">Place order</button></p>
              </form>
            </section>
            <p id="confirmation" hidden></p>
          </main>
          <script>
            const cart = [];
            const count = document.getElementById('cart-count');
            const total = document.getElementById('cart-total');
            const cartBox = document.getElementById('cart');
            const search = document.getElementById('search');

            function renderCart() {
              count.textContent = String(cart.length);
              const sum = cart.reduce((value, item) => value + item.price, 0);
              total.textContent = '$' + sum.toFixed(2);
              cartBox.hidden = cart.length === 0;
            }

            document.querySelectorAll('.add').forEach(button => {
              button.addEventListener('click', () => {
                const product = button.closest('.product');
                cart.push({
                  name: product.dataset.name,
                  price: Number(product.dataset.price)
                });
                renderCart();
              });
            });

            search.addEventListener('input', () => {
              const query = search.value.trim().toLowerCase();
              document.querySelectorAll('.product').forEach(product => {
                product.hidden = !product.dataset.name.toLowerCase().includes(query);
              });
            });
          </script>
        </body>
        </html>
        """;

    private const string ReceiptHtml = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Order confirmed</title></head>
        <body style="font:16px/1.5 system-ui,sans-serif;max-width:720px;margin:40px auto">
          <h1>Order confirmed</h1>
          <p>Thank you. Your order is being prepared.</p>
          <p><a href="/">Back to the store</a></p>
        </body>
        </html>
        """;
}
