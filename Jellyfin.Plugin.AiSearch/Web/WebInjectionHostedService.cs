using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Web;

/// <summary>
/// Best-effort extra: injects (and removes) a single script tag into the web
/// client's index.html on disk at startup, backing up the original file when
/// the web folder allows it. <see cref="IndexHtmlScriptMiddleware"/> adds the
/// same tag while Jellyfin serves the page, which is what makes the search UI
/// load even when this cannot write to disk.
/// </summary>
public class WebInjectionHostedService : IHostedService
{
    private const string Begin = "<!-- AiSearch:begin -->";
    private const string End = "<!-- AiSearch:end -->";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebInjectionHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjectionHostedService"/> class.
    /// </summary>
    public WebInjectionHostedService(IServerApplicationPaths paths, ILogger<WebInjectionHostedService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private string IndexPath => Path.Combine(_paths.WebPath, "index.html");

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Script not needed: keep the on-disk copy clean too, so it does not
            // carry a dead tag left over from before the options changed.
            if (IndexHtmlScriptMiddleware.ScriptWanted(Plugin.Instance!.Configuration))
            {
                Inject();
            }
            else
            {
                Remove();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiSearch: could not write the script tag into index.html, it is added when the page is served instead.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Remove();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiSearch: could not remove client script from web index.html.");
        }

        return Task.CompletedTask;
    }

    private void Inject()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            _logger.LogWarning("AiSearch: web index.html not found at {Path}.", path);
            return;
        }

        var html = File.ReadAllText(path);
        if (html.Contains(Begin, StringComparison.Ordinal))
        {
            return;
        }

        TryBackup(path, path + ".aisearch.bak");

        var snippet = "\n" + Begin + "\n<script src=\"../AiSearch/ClientScript\" defer></script>\n" + End + "\n";
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = idx >= 0 ? html.Insert(idx, snippet) : html + snippet;

        File.WriteAllText(path, html);
        _logger.LogInformation("AiSearch: injected client script into {Path}.", path);
    }

    private void TryBackup(string path, string backup)
    {
        // The backup is only a convenience. Some images (linuxserver) make the
        // web folder root-owned while index.html itself is writable, so a
        // failed copy must not stop the injection.
        try
        {
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "AiSearch: could not back up {Path}, injecting without a backup.", path);
        }
    }

    private void Remove()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            return;
        }

        var html = File.ReadAllText(path);
        var start = html.IndexOf(Begin, StringComparison.Ordinal);
        var end = html.IndexOf(End, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            html = html.Remove(start, (end - start) + End.Length);
            File.WriteAllText(path, html);
        }
    }
}
