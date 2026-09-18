using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.AiSearch.Web;

/// <summary>
/// Adds the plugin's client script tag to the web client's index.html while
/// Jellyfin serves it, so nothing on disk has to be writable. When
/// <see cref="Configuration.PluginConfiguration.NativeSearchOnly"/> is on, does
/// the opposite: strips any AiSearch script tag from the served page instead,
/// whatever left it there (this middleware, the on-disk injector, or by hand).
/// </summary>
public sealed class IndexHtmlScriptMiddleware
{
    private const string ScriptMarker = "AiSearch/ClientScript";
    private const string Snippet = "\n<!-- AiSearch:begin -->\n<script src=\"../AiSearch/ClientScript\" defer></script>\n<!-- AiSearch:end -->\n";
    private const string BeginMarker = "<!-- AiSearch:begin -->";
    private const string EndMarker = "<!-- AiSearch:end -->";

    // Matches a bare <script ...AiSearch/ClientScript...></script> tag that was
    // not wrapped in the begin/end markers above (hand-added, or an older
    // version's format), so it can be stripped too under NativeSearchOnly.
    private static readonly Regex BareScriptTag = new(
        "<script\\b[^>]*\\bsrc\\s*=\\s*[\"'][^\"']*AiSearch/ClientScript[^\"']*[\"'][^>]*>\\s*</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly RequestDelegate _next;
    private readonly ILogger<IndexHtmlScriptMiddleware> _logger;
    private int _logged;

    /// <summary>Initializes a new instance of the <see cref="IndexHtmlScriptMiddleware"/> class.</summary>
    public IndexHtmlScriptMiddleware(RequestDelegate next, ILogger<IndexHtmlScriptMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Invokes the middleware for one request.</summary>
    /// <param name="context">The current request's HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || !IsIndexHtmlRequest(request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask the inner pipeline for the full, uncompressed file: a cached 304
        // or a compressed body would leave nothing usable to rewrite.
        request.Headers.Remove(HeaderNames.AcceptEncoding);
        request.Headers.Remove(HeaderNames.IfNoneMatch);
        request.Headers.Remove(HeaderNames.IfModifiedSince);
        request.Headers.Remove(HeaderNames.IfRange);
        request.Headers.Remove(HeaderNames.Range);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        var bytes = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && context.Response.ContentType is not null
            && context.Response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            // Read live: the option can be flipped without a restart, so every
            // request re-checks it rather than trusting a cached decision.
            bytes = Plugin.Instance!.Configuration.NativeSearchOnly
                ? StripScriptTag(context, bytes)
                : AddScriptTag(context, bytes);
        }

        if (bytes.Length == 0)
        {
            return;
        }

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexHtmlRequest(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
    }

    private byte[] AddScriptTag(HttpContext context, byte[] original)
    {
        try
        {
            var html = Encoding.UTF8.GetString(original);
            if (html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
            {
                // Already tagged, whether by this middleware, an older on-disk
                // injection, or by hand. Leave it alone.
                return original;
            }

            var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = bodyIndex >= 0 ? html.Insert(bodyIndex, Snippet) : html + Snippet;
            var updated = Encoding.UTF8.GetBytes(html);

            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.ContentLength = updated.Length;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
            {
                _logger.LogInformation("AiSearch: adding the client script to index.html as it is served.");
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not add the client script to index.html.");
            return original;
        }
    }

    private byte[] StripScriptTag(HttpContext context, byte[] original)
    {
        try
        {
            var html = Encoding.UTF8.GetString(original);
            if (!html.Contains(ScriptMarker, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            var updatedHtml = html;
            int start;
            while ((start = updatedHtml.IndexOf(BeginMarker, StringComparison.Ordinal)) >= 0)
            {
                var end = updatedHtml.IndexOf(EndMarker, start, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                updatedHtml = updatedHtml.Remove(start, (end - start) + EndMarker.Length);
            }

            updatedHtml = BareScriptTag.Replace(updatedHtml, string.Empty);

            if (string.Equals(updatedHtml, html, StringComparison.Ordinal))
            {
                return original;
            }

            var updated = Encoding.UTF8.GetBytes(updatedHtml);

            context.Response.Headers.Remove(HeaderNames.ETag);
            context.Response.Headers.Remove(HeaderNames.LastModified);
            context.Response.ContentLength = updated.Length;

            if (Interlocked.Exchange(ref _logged, 1) == 0)
            {
                _logger.LogInformation("AiSearch: native search only is on; removing the client script from index.html as it is served.");
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not remove the client script from index.html.");
            return original;
        }
    }
}
