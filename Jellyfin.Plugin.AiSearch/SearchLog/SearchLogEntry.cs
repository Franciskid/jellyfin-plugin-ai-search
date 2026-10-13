using System.Collections.Generic;

namespace Jellyfin.Plugin.AiSearch.SearchLog;

/// <summary>
/// One logged search: a native Jellyfin search (<c>kind: "search"</c>, captured
/// by <see cref="SearchLogFilter"/>) or an AI popup search (<c>kind: "ai"</c>,
/// captured by the controller). Serialized as one compact camelCase JSON object
/// per line by <see cref="SearchLogWriter"/>; <see cref="Id"/> is assigned by
/// the writer, not by whoever builds the entry.
/// </summary>
public sealed class SearchLogEntry
{
    /// <summary>Gets or sets the strictly increasing log id, assigned by the single writer.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the ISO-8601 UTC timestamp (milliseconds) the request started.</summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry kind: <c>"search"</c> (native Jellyfin search) or <c>"ai"</c> (AI popup).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the request path, without the base URL prefix, e.g. <c>"/Items"</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP method.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the response status code.</summary>
    public int StatusCode { get; set; }

    /// <summary>Gets or sets how long the request took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the trimmed search term.</summary>
    public string SearchTerm { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective caller's user id (32-hex "N" format), or <c>null</c> if unknown.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the resolved user name, or <c>null</c> if unresolvable.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets a value indicating whether the call was authenticated with an API key.</summary>
    public bool IsApiKey { get; set; }

    /// <summary>Gets or sets the calling client's name.</summary>
    public string? Client { get; set; }

    /// <summary>Gets or sets the calling client's version.</summary>
    public string? ClientVersion { get; set; }

    /// <summary>Gets or sets the calling device's name.</summary>
    public string? Device { get; set; }

    /// <summary>Gets or sets the calling device's id.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the caller's remote IP address.</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>Gets or sets the caller's User-Agent header.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets every query-string parameter as a string, credentials redacted.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Gets or sets the <c>parentId</c> query parameter (32-hex), or <c>null</c>.</summary>
    public string? ParentId { get; set; }

    /// <summary>Gets or sets the name of the item at <see cref="ParentId"/>, or <c>null</c>.</summary>
    public string? ParentName { get; set; }

    /// <summary>Gets or sets the number of items actually in this response.</summary>
    public int ResultCount { get; set; }

    /// <summary>Gets or sets the total matching record count reported by the response, or <c>null</c> when the shape carries none.</summary>
    public int? TotalRecordCount { get; set; }

    /// <summary>Gets or sets how many of <see cref="ResultCount"/> were actually recorded in <see cref="Results"/> (capped at 250).</summary>
    public int ResultsLogged { get; set; }

    /// <summary>Gets or sets the recorded results, capped at 250 per entry.</summary>
    public List<SearchLogResultEntry> Results { get; set; } = new();

    /// <summary>Gets or sets the native-search semantic trace for this request, or <c>null</c> when none ran.</summary>
    public SearchLogSemanticSummary? Semantic { get; set; }

    /// <summary>Gets or sets the AI popup mode ("normal"/"surprise"), or <c>null</c> for <c>kind: "search"</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the AI popup's answer text, or <c>null</c> for <c>kind: "search"</c> or retrieval-only.</summary>
    public string? Answer { get; set; }
}

/// <summary>One result row within a <see cref="SearchLogEntry"/>.</summary>
public sealed class SearchLogResultEntry
{
    /// <summary>Gets or sets the 1-based position in the response.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets the item id (32-hex "N" format).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the item's <c>BaseItemKind</c>, as its string name.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the item's production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the parent series' name, for an episode.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the season number (<c>ParentIndexNumber</c>), for an episode.</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number (<c>IndexNumber</c>), for an episode.</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets a value indicating whether this item was a semantic (not title) match.</summary>
    public bool Semantic { get; set; }

    /// <summary>
    /// Gets or sets where a native-search result came from: <c>"title"</c>
    /// (Jellyfin's own title search), <c>"semantic"</c> (only the plugin's
    /// meaning match found it) or <c>"both"</c>. <c>null</c> for AI popup rows.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>Gets or sets the raw semantic similarity, when <see cref="Semantic"/> is <c>true</c>.</summary>
    public float? SemanticScore { get; set; }

    /// <summary>Gets or sets the 1-based semantic rank, when <see cref="Semantic"/> is <c>true</c>.</summary>
    public int? SemanticRank { get; set; }

    /// <summary>Gets or sets the AI popup's one-line reason for this result, or <c>null</c> for native search.</summary>
    public string? Reason { get; set; }
}

/// <summary>A <see cref="Search.SemanticSearchTrace"/>, flattened for the search log.</summary>
public sealed class SearchLogSemanticSummary
{
    /// <summary>Gets or sets a value indicating whether semantic matching is turned on in configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider ran retrieval for this query.</summary>
    public bool Ran { get; set; }

    /// <summary>Gets or sets why retrieval did not run, when <see cref="Ran"/> is <c>false</c>.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>Gets or sets the retrieval source used: <c>"local"</c> or <c>"remote"</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets how long retrieval took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the time spent on the backend (query embedding or remote endpoint), in milliseconds.</summary>
    public long BackendMs { get; set; }

    /// <summary>Gets or sets the time spent scanning the local index, in milliseconds.</summary>
    public long ScanMs { get; set; }

    /// <summary>Gets or sets the time spent loading and visibility-checking hits, in milliseconds.</summary>
    public long LookupMs { get; set; }

    /// <summary>Gets or sets a value indicating whether the backend answer came from the plugin's cache.</summary>
    public bool Cached { get; set; }

    /// <summary>Gets or sets how many raw hits retrieval returned before filtering.</summary>
    public int RawHits { get; set; }

    /// <summary>Gets or sets how many hits survived filtering.</summary>
    public int Matches { get; set; }

    /// <summary>Gets or sets the failure reason, when retrieval threw.</summary>
    public string? Error { get; set; }
}
