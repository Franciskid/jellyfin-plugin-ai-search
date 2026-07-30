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
    /// Resolves the embeddings target from the plugin configuration. The local
    /// index needs a dedicated embeddings endpoint and model; when either is
    /// missing the index is disabled and the plugin falls back to a catalog slice.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>The target, or <c>null</c> when the local index is not configured.</returns>
    public static EmbeddingsTarget? FromConfiguration(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.EmbeddingModel)
            || string.IsNullOrWhiteSpace(config.EmbeddingEndpointUrl))
        {
            return null;
        }

        return new EmbeddingsTarget(
            config.EmbeddingEndpointUrl.Trim().TrimEnd('/'),
            config.EmbeddingApiKey?.Trim() ?? string.Empty,
            config.EmbeddingModel.Trim(),
            Math.Clamp(config.TimeoutSeconds, 5, 120));
    }
}
