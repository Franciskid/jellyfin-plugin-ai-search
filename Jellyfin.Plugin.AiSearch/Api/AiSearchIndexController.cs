using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>
/// Admin endpoints for the semantic index, used by the plugin's config page:
/// read the index status and kick a rebuild without leaving the dashboard.
/// </summary>
[ApiController]
[Route("AiSearch")]
[Authorize(AuthenticationSchemes = "CustomAuthentication")]
public class AiSearchIndexController : ControllerBase
{
    private readonly IndexBuilder _builder;
    private readonly VectorIndex _index;
    private readonly ILogger<AiSearchIndexController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AiSearchIndexController"/> class.</summary>
    /// <param name="builder">The index builder.</param>
    /// <param name="index">The in-memory index.</param>
    /// <param name="logger">Logger.</param>
    public AiSearchIndexController(IndexBuilder builder, VectorIndex index, ILogger<AiSearchIndexController> logger)
    {
        _builder = builder;
        _index = index;
        _logger = logger;
    }

    /// <summary>Current semantic-index state (admin only; feeds the config page).</summary>
    /// <returns>Configuration, size, and last-build info.</returns>
    [HttpGet("IndexStatus")]
    public ActionResult IndexStatus()
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        return Ok(BuildStatus());
    }

    /// <summary>Starts an index build in the background (admin only). No-op when one is already running.</summary>
    /// <returns>The status right after the build was started.</returns>
    [HttpPost("RebuildIndex")]
    public ActionResult RebuildIndex()
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        // Fire and forget: the config page polls IndexStatus for progress.
        // IndexBuilder is single-flight, so double-clicks are harmless.
        _ = Task.Run(() => _builder.BuildAsync(null, CancellationToken.None));
        _logger.LogInformation("AiSearch: index rebuild requested from the config page.");
        return Accepted(BuildStatus(building: true));
    }

    private object BuildStatus(bool building = false)
    {
        var config = Plugin.Instance!.Configuration;
        var target = EmbeddingsTarget.FromConfiguration(config);
        var snapshot = _index.Current;
        var lastRun = _builder.LastRun;
        return new
        {
            enabled = config.SemanticEnabled,
            configured = target is not null,
            model = target?.Model,
            entries = snapshot?.Entries.Count ?? 0,
            dimensions = snapshot?.Dimensions ?? 0,
            indexModel = snapshot?.Model,
            builtAt = snapshot?.BuiltAt,
            building = building || _builder.IsBuilding,
            lastRun = lastRun is null
                ? null
                : new
                {
                    startedAt = lastRun.StartedAt,
                    finishedAt = lastRun.FinishedAt,
                    embedded = lastRun.Embedded,
                    reused = lastRun.Reused,
                    total = lastRun.Total,
                    error = lastRun.Error,
                },
        };
    }
}
