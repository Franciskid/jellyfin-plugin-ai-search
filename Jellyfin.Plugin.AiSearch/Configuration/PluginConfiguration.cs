using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiSearch.Configuration;

/// <summary>
/// Plugin configuration, editable from the Jellyfin dashboard. Two modes:
/// <c>platform</c> routes through a self-hosted AI platform that maintains a
/// semantic index of the library (plus usage history), and <c>direct</c> calls
/// any OpenAI-compatible endpoint with a locally-built candidate list (no
/// extra infrastructure needed).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the AI search feature is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the mode: "platform" or "direct".</summary>
    public string Mode { get; set; } = "platform";

    // --- Platform mode ---

    /// <summary>
    /// Gets or sets the base URL of the platform API. The plugin sends the
    /// prompt + user and the platform performs a semantic search over the library.
    /// </summary>
    public string PlatformApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the application API key issued on the platform's Applications page.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many candidates semantic retrieval hands to the model
    /// per query (platform mode, and direct mode when the local index is on).
    /// </summary>
    public int MaxRetrieve { get; set; } = 40;

    // --- Direct mode ---

    /// <summary>
    /// Gets or sets the base URL of an OpenAI-compatible endpoint (OpenAI, Ollama,
    /// OpenRouter, LiteLLM, …). The plugin builds the prompt + candidate list and
    /// calls it directly.
    /// </summary>
    public string DirectEndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the bearer key for the direct endpoint.</summary>
    public string DirectApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many candidate movies are sent to the direct model when
    /// no semantic index is available (the fallback path).
    /// </summary>
    public int MaxCatalogItems { get; set; } = 350;

    /// <summary>
    /// Gets or sets how fallback candidates are chosen: "top" (best community
    /// rating), "random" (a random sample), or "mix" (half top, half random).
    /// </summary>
    public string SelectionStrategy { get; set; } = "top";

    // --- Direct mode: local semantic index ---

    /// <summary>
    /// Gets or sets a value indicating whether direct mode retrieves candidates
    /// from a local embedding index of the library (built by the "Build AI
    /// Search index" scheduled task) instead of dumping a catalog slice.
    /// </summary>
    public bool SemanticEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether TV series and their episodes are
    /// embedded into the semantic index too, enabling the "TV Shows" search
    /// scope. Off by default: episode counts can be large (a one-time build that
    /// can run for hours). Movies are always indexed.
    /// </summary>
    public bool IndexTvShows { get; set; }

    /// <summary>
    /// Gets or sets the embedding model id (e.g. "bge-m3" on Ollama,
    /// "text-embedding-3-small" on OpenAI). Empty disables the index.
    /// </summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenAI-compatible endpoint used for embeddings. Empty
    /// means "same as the direct endpoint" — set it when the chat provider has
    /// no /v1/embeddings (e.g. OpenRouter) and embeddings come from elsewhere.
    /// </summary>
    public string EmbeddingEndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the key for the embedding endpoint (empty = reuse the direct key; local Ollama needs none).</summary>
    public string EmbeddingApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a prefix prepended to queries at search time. Some models
    /// need one (nomic-embed-text: "search_query: "); bge-m3 needs none.
    /// </summary>
    public string EmbeddingQueryPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets a prefix prepended to documents at index time (nomic-embed-text: "search_document: ").</summary>
    public string EmbeddingDocumentPrefix { get; set; } = string.Empty;

    // --- Shared ---

    /// <summary>Gets or sets the model alias/id used for recommendations.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of recommendations returned.</summary>
    public int MaxResults { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum synopsis length (in characters) sent per
    /// candidate to the model. A value of 0 or less means no limit — the full
    /// synopsis is sent uncropped.
    /// </summary>
    public int SynopsisMaxLength { get; set; } = 180;

    /// <summary>Gets or sets a value indicating whether already-watched movies may be recommended.</summary>
    public bool IncludeWatched { get; set; }

    /// <summary>Gets or sets the request timeout, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 45;
}
