using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// The query side of the semantic index: embed the prompt, return the most
/// similar item ids. Everything user-specific (visibility, watched state)
/// stays with the caller, which knows the user.
/// </summary>
public class SemanticRetriever
{
    private readonly EmbeddingsClient _embeddings;
    private readonly VectorIndex _index;
    private readonly ILogger<SemanticRetriever> _logger;

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
    /// Retrieves the ids most similar to the prompt, best first — or <c>null</c>
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
        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null || !_index.IsUsable(target.Model))
        {
            return null;
        }

        try
        {
            var query = (config.EmbeddingQueryPrefix ?? string.Empty) + prompt;
            var vectors = await _embeddings.EmbedAsync(target, new[] { query }, cancellationToken).ConfigureAwait(false);
            var vector = vectors[0];
            VectorMath.NormalizeInPlace(vector);

            var hits = _index.Search(vector, take, allowedIds);
            var ids = new List<Guid>(hits.Count);
            foreach (var (itemId, _) in hits)
            {
                ids.Add(itemId);
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Retrieval is an enhancement: log and let the caller fall back.
            _logger.LogWarning(ex, "AiSearch: semantic retrieval failed; falling back to catalog selection.");
            return null;
        }
    }
}
