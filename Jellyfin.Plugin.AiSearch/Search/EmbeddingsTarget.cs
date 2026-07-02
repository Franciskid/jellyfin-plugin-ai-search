using System;
using Jellyfin.Plugin.AiSearch.Configuration;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Where and how to compute embeddings: an OpenAI-compatible base URL, an
/// optional bearer key (local Ollama needs none), and the embedding model id.
/// </summary>
public sealed record EmbeddingsTarget(string BaseUrl, string ApiKey, string Model, int TimeoutSeconds)
{
    /// <summary>
    /// Resolves the embeddings target from the plugin configuration, falling
    /// back to the direct-mode chat endpoint/key when no dedicated embedding
    /// endpoint is set (the common case: one endpoint serves both).
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>The target, or <c>null</c> when semantic search is disabled or not configured.</returns>
    public static EmbeddingsTarget? FromConfiguration(PluginConfiguration config)
    {
        if (!config.SemanticEnabled || string.IsNullOrWhiteSpace(config.EmbeddingModel))
        {
            return null;
        }

        var baseUrl = FirstNonEmpty(config.EmbeddingEndpointUrl, config.DirectEndpointUrl);
        if (baseUrl is null)
        {
            return null;
        }

        var apiKey = FirstNonEmpty(config.EmbeddingApiKey, config.DirectApiKey) ?? string.Empty;
        return new EmbeddingsTarget(
            baseUrl.TrimEnd('/'),
            apiKey,
            config.EmbeddingModel.Trim(),
            Math.Clamp(config.TimeoutSeconds, 5, 120));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
