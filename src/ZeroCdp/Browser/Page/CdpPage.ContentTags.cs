using ZeroCdp.Protocol;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    /// <summary>
    /// Adds a script element to the document and resolves after an external script has loaded.
    /// Exactly one of <see cref="PageAddScriptTagOptions.Url"/>,
    /// <see cref="PageAddScriptTagOptions.Path"/> or
    /// <see cref="PageAddScriptTagOptions.Content"/> must be set.
    /// </summary>
    /// <remarks>
    /// The script is appended to the page's DOM, so it executes in the page's main world. The
    /// command itself is evaluated in the isolated world and never enables the Runtime domain.
    /// Inline scripts remain subject to the page's Content-Security-Policy unless
    /// <see cref="BrowserContextOptions.BypassCsp"/> is enabled.
    /// </remarks>
    public async ValueTask AddScriptTagAsync(
        PageAddScriptTagOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        string expression = await ContentTagScripts.BuildScriptAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await _isolated.EvaluateAsync(expression, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Adding the script tag failed.");
        }
    }

    /// <summary>Adds a script tag using a URL, local path or inline content.</summary>
    public ValueTask AddScriptTagAsync(
        string content,
        CancellationToken cancellationToken = default)
        => AddScriptTagAsync(new PageAddScriptTagOptions { Content = content }, cancellationToken);

    /// <summary>
    /// Adds a style element or stylesheet link to the document and resolves after it has loaded.
    /// Exactly one of <see cref="PageAddStyleTagOptions.Url"/>,
    /// <see cref="PageAddStyleTagOptions.Path"/> or
    /// <see cref="PageAddStyleTagOptions.Content"/> must be set.
    /// </summary>
    public async ValueTask AddStyleTagAsync(
        PageAddStyleTagOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        string expression = await ContentTagScripts.BuildStyleAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await EnableAsync(cancellationToken).ConfigureAwait(false);
        EvaluationResult result = await _isolated.EvaluateAsync(expression, cancellationToken)
            .ConfigureAwait(false);
        if (result.Threw)
        {
            throw new CdpException(result.ExceptionSummary ?? "Adding the style tag failed.");
        }
    }

    /// <summary>Adds an inline style tag.</summary>
    public ValueTask AddStyleTagAsync(
        string content,
        CancellationToken cancellationToken = default)
        => AddStyleTagAsync(new PageAddStyleTagOptions { Content = content }, cancellationToken);
}

internal static class ContentTagScripts
{
    internal static async ValueTask<string> BuildScriptAsync(
        PageAddScriptTagOptions options,
        CancellationToken cancellationToken)
    {
        int sources = CountSources(options.Url, options.Path, options.Content);
        if (sources != 1)
        {
            throw new ArgumentException(
                "Exactly one of Url, Path or Content must be set.",
                nameof(options));
        }

        string type = string.IsNullOrEmpty(options.Type) ? string.Empty : options.Type;

        if (!string.IsNullOrEmpty(options.Url))
        {
            return ExternalElementScript("script", "src", options.Url, type);
        }

        string content = options.Content ?? string.Empty;
        if (!string.IsNullOrEmpty(options.Path))
        {
            content = await File.ReadAllTextAsync(options.Path, cancellationToken).ConfigureAwait(false);
        }

        return $$"""
            (() => {
              const element = document.createElement('script');
              element.type = {{JsText.Quote(type.Length == 0 ? "text/javascript" : type)}};
              element.textContent = {{JsText.Quote(content)}};
              (document.head || document.documentElement).appendChild(element);
              return true;
            })()
            """;
    }

    internal static async ValueTask<string> BuildStyleAsync(
        PageAddStyleTagOptions options,
        CancellationToken cancellationToken)
    {
        int sources = CountSources(options.Url, options.Path, options.Content);
        if (sources != 1)
        {
            throw new ArgumentException(
                "Exactly one of Url, Path or Content must be set.",
                nameof(options));
        }

        if (!string.IsNullOrEmpty(options.Url))
        {
            return ExternalElementScript("link", "href", options.Url, type: null, isStyle: true);
        }

        string content = options.Content ?? string.Empty;
        if (!string.IsNullOrEmpty(options.Path))
        {
            content = await File.ReadAllTextAsync(options.Path, cancellationToken).ConfigureAwait(false);
        }

        return $$"""
            (() => {
              const element = document.createElement('style');
              element.textContent = {{JsText.Quote(content)}};
              (document.head || document.documentElement).appendChild(element);
              return true;
            })()
            """;
    }

    internal static int CountSources(string? url, string? path, string? content)
    {
        int count = 0;
        if (!string.IsNullOrEmpty(url))
        {
            count++;
        }

        if (!string.IsNullOrEmpty(path))
        {
            count++;
        }

        if (content is not null)
        {
            count++;
        }

        return count;
    }

    private static string ExternalElementScript(
        string tag,
        string sourceAttribute,
        string source,
        string? type,
        bool isStyle = false)
    {
        string typeLine = type is null
            ? string.Empty
            : $"element.type = {JsText.Quote(type)};";
        string relLine = isStyle ? "element.rel = 'stylesheet';" : string.Empty;

        return $$"""
            new Promise((resolve, reject) => {
              const element = document.createElement({{JsText.Quote(tag)}});
              {{typeLine}}
              {{relLine}}
              element.{{sourceAttribute}} = {{JsText.Quote(source)}};
              element.addEventListener('load', () => resolve(true), { once: true });
              element.addEventListener('error', () => reject(new Error('Failed to load ' + {{JsText.Quote(source)}})), { once: true });
              (document.head || document.documentElement).appendChild(element);
            })
            """;
    }
}
