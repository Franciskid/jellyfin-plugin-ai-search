using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Web;

/// <summary>
/// Injects (and removes) a single script tag into the web client's index.html so
/// the AI search UI loads. The original file is backed up and the change is
/// idempotent and reversible.
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
            Inject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not inject client script into web index.html. The API still works; add <script src=\"/AiSearch/ClientScript\"> manually if needed.");
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

        var backup = path + ".aisearch.bak";
        if (!File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        var snippet = "\n" + Begin + "\n<script src=\"/AiSearch/ClientScript\" defer></script>\n" + End + "\n";
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = idx >= 0 ? html.Insert(idx, snippet) : html + snippet;

        File.WriteAllText(path, html);
        _logger.LogInformation("AiSearch: injected client script into {Path}.", path);
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
