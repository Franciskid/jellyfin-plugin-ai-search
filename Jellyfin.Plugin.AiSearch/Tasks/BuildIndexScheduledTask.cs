using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AiSearch.Tasks;

/// <summary>
/// Nightly "Build AI Search index" task (Dashboard → Scheduled Tasks). Keeps
/// the semantic index in sync with the library: unchanged movies are skipped,
/// so the routine run costs almost nothing.
/// </summary>
public class BuildIndexScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IndexBuilder _builder;

    /// <summary>Initializes a new instance of the <see cref="BuildIndexScheduledTask"/> class.</summary>
    /// <param name="builder">The index builder.</param>
    public BuildIndexScheduledTask(IndexBuilder builder)
    {
        _builder = builder;
    }

    /// <inheritdoc />
    public string Name => "Build AI Search index";

    /// <inheritdoc />
    public string Key => "AiSearchBuildIndex";

    /// <inheritdoc />
    public string Description => "Embeds new or changed movies for the AI Search semantic index (direct mode).";

    /// <inheritdoc />
    public string Category => "AI Search";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _builder.BuildAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 3 AM local, when the library is quiet.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }
}
