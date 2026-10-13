using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Keeps the retrieval backend warm. Embedding servers such as Ollama unload
/// an idle model after a few minutes, and reloading it costs seconds, which
/// used to land on the first search of every session. The client script calls
/// this when the search page or the AI popup opens, and every native search
/// touches it too, so after an idle period a throwaway request loads the model
/// while the user is still typing.
/// </summary>
public sealed class SemanticWarmer
{
    // Anything used more recently than this is assumed to still be loaded
    // (Ollama's default keep-alive is five minutes).
    private static readonly TimeSpan IdleBeforeWarm = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinWarmInterval = TimeSpan.FromSeconds(60);

    private readonly SemanticRetriever _retriever;
    private readonly RemoteSearchClient _remoteSearchClient;
    private readonly ILogger<SemanticWarmer> _logger;
    private long _lastUsedTicks;
    private long _lastWarmTicks;

    /// <summary>Initializes a new instance of the <see cref="SemanticWarmer"/> class.</summary>
    /// <param name="retriever">The local index retriever (owns the embeddings call).</param>
    /// <param name="remoteSearchClient">The remote search endpoint client.</param>
    /// <param name="logger">Logger.</param>
    public SemanticWarmer(SemanticRetriever retriever, RemoteSearchClient remoteSearchClient, ILogger<SemanticWarmer> logger)
    {
        _retriever = retriever;
        _remoteSearchClient = remoteSearchClient;
        _logger = logger;
    }

    /// <summary>Records that the backend just answered a real query, so it is warm.</summary>
    public void MarkUsed() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Starts a background warm-up call when the backend has been idle long
    /// enough to be cold, at most once a minute. Returns immediately.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns><c>true</c> when a warm-up call was started.</returns>
    public bool Touch(PluginConfiguration config)
    {
        if (!config.Enabled)
        {
            return false;
        }

        var isRemote = string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase);
        if (isRemote ? string.IsNullOrWhiteSpace(config.SearchEndpointUrl) : EmbeddingsTarget.FromConfiguration(config) is null)
        {
            return false;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastUsedTicks) < IdleBeforeWarm.Ticks)
        {
            return false;
        }

        var lastWarm = Interlocked.Read(ref _lastWarmTicks);
        if (now - lastWarm < MinWarmInterval.Ticks
            || Interlocked.CompareExchange(ref _lastWarmTicks, now, lastWarm) != lastWarm)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            if (isRemote)
            {
                await WarmRemoteAsync(config).ConfigureAwait(false);
            }
            else
            {
                await _retriever.WarmUpAsync(config).ConfigureAwait(false);
            }

            MarkUsed();
        });
        return true;
    }

    private async Task WarmRemoteAsync(PluginConfiguration config)
    {
        try
        {
            var payload = new
            {
                query = "warm up",
                scope = "movies",
                maxResults = 1,
                includeWatched = true,
                excludeItemIds = Array.Empty<string>(),
                client = new { name = "jellyfin-ai-search", version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0" }
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120)));
            await _remoteSearchClient.SearchAsync(config.SearchEndpointUrl, config.SearchApiKey, config.TimeoutSeconds, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiSearch: remote search warm-up failed.");
        }
    }
}
