using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiSearch.Configuration;

/// <summary>
/// Plugin configuration, editable from the Jellyfin dashboard.
///
/// <para>The plugin always runs the same flow: it narrows your library to a
/// shortlist of candidate titles ("retrieval"), then asks a language model to
/// pick and explain the best matches. Only two things must be configured:</para>
///
/// <list type="number">
///   <item><description><b>A language model (chat) endpoint</b>, any
///   OpenAI-compatible <c>/v1/chat/completions</c> endpoint, or a platform's
///   <c>/api/media/chat</c>. Always required.</description></item>
///   <item><description><b>A retrieval source</b>, either <b>Local</b> (the
///   plugin builds and queries its own embedding index on this server) or
///   <b>Remote</b> (a service returns candidates for a query).</description></item>
/// </list>
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the AI search feature is enabled.</summary>
    public bool Enabled { get; set; } = true;

    // 1. Language model (chat), always required

    /// <summary>
    /// Gets or sets the full URL of the chat-completions endpoint the plugin
    /// calls to pick and explain titles. Any OpenAI-compatible endpoint works,
    /// e.g. <c>https://api.openai.com/v1/chat/completions</c>,
    /// <c>http://localhost:11434/v1/chat/completions</c> (Ollama), or a platform
    /// <c>https://ai.example.com/api/media/chat</c>. This is the complete URL -
    /// the plugin does not append a path.
    /// </summary>
    public string ChatEndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the bearer key for the chat endpoint (blank if it needs none).</summary>
    public string ChatApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the model alias/id used for recommendations.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional URL that lists available models, to populate the
    /// dashboard dropdown (e.g. <c>.../v1/models</c> or <c>.../api/media/models</c>).
    /// Leave blank to just type the model id.
    /// </summary>
    public string ModelsEndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional cheaper model for the "Help me choose" interview
    /// (a small request). Blank reuses <see cref="Model"/>.
    /// </summary>
    public string InterviewModel { get; set; } = string.Empty;

    // 2. Retrieval, where candidate titles come from

    /// <summary>
    /// Gets or sets the retrieval source: <c>"local"</c> (build+query an index on
    /// this server) or <c>"remote"</c> (a service returns candidates).
    /// </summary>
    public string RetrievalMode { get; set; } = "local";

    // 2a. Remote retrieval

    /// <summary>
    /// Gets or sets the full URL of a retrieval endpoint that takes a query and
    /// returns ranked library candidates (no model call), e.g.
    /// <c>https://ai.example.com/api/media/search</c>. Used when
    /// <see cref="RetrievalMode"/> is <c>"remote"</c>.
    /// </summary>
    public string SearchEndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the bearer key for the remote search endpoint.</summary>
    public string SearchApiKey { get; set; } = string.Empty;

    // 2b. Local retrieval, semantic index built on this server

    /// <summary>
    /// Gets or sets a value indicating whether TV series and their episodes are
    /// embedded into the local index too (enables the "TV Shows" scope). Off by
    /// default: episode counts can be large (a one-time build that can run for
    /// hours). Movies are always indexed. Local retrieval only.
    /// </summary>
    public bool IndexTvShows { get; set; }

    /// <summary>
    /// Gets or sets the embedding model id (e.g. "bge-m3" on Ollama,
    /// "text-embedding-3-small" on OpenAI). Required for local retrieval; blank
    /// disables the local index (the plugin then sends a catalog slice instead).
    /// </summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the full URL of an OpenAI-compatible embeddings endpoint
    /// (<c>.../v1/embeddings</c>). Required for local retrieval.
    /// </summary>
    public string EmbeddingEndpointUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the key for the embedding endpoint (local Ollama needs none).</summary>
    public string EmbeddingApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional URL that lists embedding models, to populate the
    /// embedding-model dropdown (e.g. <c>.../v1/models</c> or
    /// <c>.../api/media/models?capability=embedding</c>). Blank falls back to the
    /// main models endpoint.
    /// </summary>
    public string EmbeddingModelsEndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a prefix prepended to queries at search time. Some models
    /// need one (nomic-embed-text: "search_query: "); bge-m3 needs none.
    /// </summary>
    public string EmbeddingQueryPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets a prefix prepended to documents at index time (nomic-embed-text: "search_document: ").</summary>
    public string EmbeddingDocumentPrefix { get; set; } = string.Empty;

    // 2c. Local fallback, used until the index is built, or if embeddings fail

    /// <summary>
    /// Gets or sets how many candidate movies are sent to the model when no local
    /// index is available (the fallback path).
    /// </summary>
    public int MaxCatalogItems { get; set; } = 350;

    /// <summary>
    /// Gets or sets how fallback candidates are chosen: "top" (best community
    /// rating), "random" (a random sample), or "mix" (half top, half random).
    /// </summary>
    public string SelectionStrategy { get; set; } = "top";

    // 3. Shared options

    /// <summary>
    /// Gets or sets how many candidates retrieval hands to the model per query
    /// (both local index and remote search).
    /// </summary>
    public int MaxRetrieve { get; set; } = 40;

    /// <summary>Gets or sets the maximum number of recommendations returned.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum synopsis length (in characters) sent per
    /// candidate to the model. 0 or less means no limit (full synopsis).
    /// </summary>
    public int SynopsisMaxLength { get; set; }

    /// <summary>Gets or sets a value indicating whether already-watched titles may be recommended.</summary>
    public bool IncludeWatched { get; set; }

    /// <summary>Gets or sets the request timeout, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 45;
}
