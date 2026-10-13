using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>Ranked hits from the local index plus where the time went.</summary>
/// <param name="Hits">Item ids with their cosine similarity, best first.</param>
/// <param name="EmbedMs">Time spent getting the query vector (0 on a cache hit).</param>
/// <param name="ScanMs">Time spent scanning the index.</param>
/// <param name="CachedQuery">Whether the query vector came from the cache.</param>
public sealed record SemanticRetrieval(IReadOnlyList<(Guid ItemId, float Score)> Hits, long EmbedMs, long ScanMs, bool CachedQuery);

/// <summary>
/// The query side of the semantic index: embed the prompt, return the most
/// similar item ids. Everything user-specific (visibility, watched state)
/// stays with the caller, which knows the user.
/// </summary>
public class SemanticRetriever
{
    // Query vectors depend only on (endpoint, model, text), so they can be kept
    // for a long time; 1024 × 1k floats is ~4 MB at most.
    private static readonly TimeSpan VectorTtl = TimeSpan.FromHours(12);

    private readonly EmbeddingsClient _embeddings;
    private readonly VectorIndex _index;
    private readonly ILogger<SemanticRetriever> _logger;
    private readonly CoalescingCache<float[]> _queryVectors = new(1024, VectorTtl);

    /// <summary>Initializes a new instance of the <see cref="SemanticRetriever"/> class.</summary>
    /// <param name="embeddings">Embeddings client.</param>
    /// <param name="index">The in-memory index.</param>
    /// <param name="logger">Logger.</param>
    public SemanticRetriever(EmbeddingsClient embeddings, VectorIndex index, ILogger<SemanticRetriever> logger)
    {
        _embeddings = embeddings;
        _index = index;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the ids most similar to the prompt, best first, or <c>null</c>
    /// when semantic search cannot serve this query (not configured, index not
    /// built / built with another model, or the embedding call failed). A
    /// <c>null</c> tells the caller to fall back to the non-semantic strategy.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="prompt">The user's natural-language prompt.</param>
    /// <param name="take">How many ids to return (before user-level filtering).</param>
    /// <param name="allowedIds">Restricts scoring to these item ids when non-empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ranked item ids, or <c>null</c> to fall back.</returns>
    public async Task<IReadOnlyList<Guid>?> TryRetrieveAsync(
        PluginConfiguration config,
        string prompt,
        int take,
        HashSet<Guid>? allowedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RetrieveAsync(config, prompt, take, allowedIds, cancellationToken).ConfigureAwait(false);
            return result?.Hits.Select(h => h.ItemId).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Retrieval is an enhancement: log and let the caller fall back.
            _logger.LogWarning(ex, "AiSearch: semantic retrieval failed; falling back to catalog selection.");
            return null;
        }
    }

    /// <summary>
    /// Retrieves scored hits for the prompt, or <c>null</c> when the local index
    /// cannot serve it (not configured, not built, or built with another model).
    /// The embedding call is shared with any identical query already running
    /// and cached afterwards, so retyping or paging a search costs one scan.
    /// Failures are thrown, for callers that report them their own way.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="prompt">The user's natural-language prompt.</param>
    /// <param name="take">How many hits to return (before user-level filtering).</param>
    /// <param name="allowedIds">Restricts scoring to these item ids when non-empty.</param>
    /// <param name="cancellationToken">
    /// Stops waiting; an embedding call already started finishes in the
    /// background and is cached for the next query.
    /// </param>
    /// <returns>Hits with timings, or <c>null</c>.</returns>
    public async Task<SemanticRetrieval?> RetrieveAsync(
        PluginConfiguration config,
        string prompt,
        int take,
        HashSet<Guid>? allowedIds,
        CancellationToken cancellationToken)
    {
        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null || !_index.IsUsable(target.Model))
        {
            return null;
        }

        var text = (config.EmbeddingQueryPrefix ?? string.Empty) + prompt.Trim();
        var stopwatch = Stopwatch.StartNew();
        var (vector, cached) = await _queryVectors
            .GetAsync(CacheKey(target, text), token => EmbedOneAsync(target, text, token), TimeSpan.FromSeconds(target.TimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);
        var embedMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        var hits = _index.Search(vector, take, allowedIds);
        return new SemanticRetrieval(hits, cached ? 0 : embedMs, stopwatch.ElapsedMilliseconds, cached);
    }

    /// <summary>
    /// Sends one throwaway embedding request so a backend that unloads idle
    /// models (Ollama does after a few minutes) is loaded before the user's
    /// real query arrives. Not cached, and never throws.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>A task that completes when the warm-up call returns.</returns>
    public async Task WarmUpAsync(PluginConfiguration config)
    {
        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(target.TimeoutSeconds));
            await _embeddings.EmbedAsync(target, new[] { (config.EmbeddingQueryPrefix ?? string.Empty) + "warm up" }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AiSearch: embedding warm-up failed.");
        }
    }

    private async Task<float[]> EmbedOneAsync(EmbeddingsTarget target, string text, CancellationToken cancellationToken)
    {
        var vectors = await _embeddings.EmbedAsync(target, new[] { text }, cancellationToken).ConfigureAwait(false);
        var vector = vectors[0];
        VectorMath.NormalizeInPlace(vector);
        return vector;
    }

    private static string CacheKey(EmbeddingsTarget target, string text) =>
        target.BaseUrl + "\n" + target.Model + "\n" + text;
}
