using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Per-request record of what <see cref="SemanticSearchProvider"/> did for a
/// native Jellyfin search, stashed in <c>HttpContext.Items</c> so the search
/// log (<see cref="Jellyfin.Plugin.AiSearch.Api.SearchLogController"/> and its
/// writer) can describe the semantic side of a result without re-running
/// retrieval.
/// </summary>
public sealed class SemanticSearchTrace
{
    /// <summary>The <c>HttpContext.Items</c> key this trace is stored under.</summary>
    public const string HttpContextKey = "AiSearch.SemanticTrace";

    /// <summary>Gets or sets a value indicating whether semantic matching is turned on in the current configuration.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider actually ran retrieval for this query.</summary>
    public bool Ran { get; set; }

    /// <summary>Gets or sets why retrieval did not run, when <see cref="Ran"/> is <c>false</c>.</summary>
    public string? SkippedReason { get; set; }

    /// <summary>Gets or sets the retrieval source used: <c>"local"</c> or <c>"remote"</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets how long retrieval took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets how many raw hits retrieval returned before filtering.</summary>
    public int RawHits { get; set; }

    /// <summary>Gets or sets how many hits survived filtering and were handed to Jellyfin.</summary>
    public int Matches { get; set; }

    /// <summary>Gets or sets the failure reason when retrieval threw, e.g. <c>"timeout"</c> or an exception message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets the returned item ids with their raw similarity and 1-based rank.</summary>
    public Dictionary<Guid, (float Similarity, int Rank)> Hits { get; } = new();
}
