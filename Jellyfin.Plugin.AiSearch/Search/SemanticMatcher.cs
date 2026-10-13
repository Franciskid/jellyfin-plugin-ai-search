using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AiSearch.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>One meaning-based match the user may see.</summary>
/// <param name="Item">The library item.</param>
/// <param name="Similarity">Raw similarity reported by retrieval.</param>
/// <param name="TitleMatch">Whether Jellyfin's own title search finds it too.</param>
public sealed record SemanticMatch(BaseItem Item, float Similarity, bool TitleMatch);

/// <summary>Matches plus where the time went.</summary>
public sealed class SemanticMatchResult
{
    /// <summary>Gets or sets the retrieval source used: <c>"local"</c> or <c>"remote"</c>.</summary>
    public string Mode { get; set; } = "local";

    /// <summary>Gets the visible matches, best first.</summary>
    public List<SemanticMatch> Matches { get; } = new();

    /// <summary>Gets or sets how many raw hits retrieval returned before filtering.</summary>
    public int RawHits { get; set; }

    /// <summary>Gets or sets the backend time (query embedding or remote endpoint), in milliseconds.</summary>
    public long BackendMs { get; set; }

    /// <summary>Gets or sets the local index scan time, in milliseconds.</summary>
    public long ScanMs { get; set; }

    /// <summary>Gets or sets the time spent loading and visibility-checking hits, in milliseconds.</summary>
    public long LookupMs { get; set; }

    /// <summary>Gets or sets a value indicating whether the backend answer came from the cache.</summary>
    public bool Cached { get; set; }
}

/// <summary>
/// Turns a search term into meaning-based matches the calling user may see:
/// retrieval (local index or remote endpoint, cached and shared between
/// identical concurrent searches), then loading, visibility and alternate-
/// version filtering, then flagging which hits are title matches anyway.
/// Shared by Jellyfin's native search provider and the web client's separate
/// AI Search row.
/// </summary>
public sealed class SemanticMatcher
{
    // Remote answers depend on the user and the text only; two minutes covers
    // retyping, backspacing and the web client's duplicate requests.
    private static readonly CoalescingCache<List<(Guid ItemId, float Score)>> RemoteCache = new(256, TimeSpan.FromMinutes(2));

    private readonly SemanticRetriever _retriever;
    private readonly RemoteSearchClient _remoteSearchClient;
    private readonly ILibraryManager _libraryManager;
    private readonly SemanticWarmer _warmer;
    private readonly LibraryScope _scope;

    /// <summary>Initializes a new instance of the <see cref="SemanticMatcher"/> class.</summary>
    /// <param name="retriever">The local index retriever.</param>
    /// <param name="remoteSearchClient">The remote search endpoint client.</param>
    /// <param name="libraryManager">Library manager, for loading and visibility-checking hits.</param>
    /// <param name="warmer">Told whenever the backend answered, so it is known to be warm.</param>
    /// <param name="scope">Leaves out libraries excluded from AI Search.</param>
    public SemanticMatcher(SemanticRetriever retriever, RemoteSearchClient remoteSearchClient, ILibraryManager libraryManager, SemanticWarmer warmer, LibraryScope scope)
    {
        _retriever = retriever;
        _remoteSearchClient = remoteSearchClient;
        _libraryManager = libraryManager;
        _warmer = warmer;
        _scope = scope;
    }

    private static string ClientVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// Finds up to <paramref name="take"/> visible matches for <paramref name="term"/>.
    /// Throws on a backend failure; an <see cref="OperationCanceledException"/>
    /// means <paramref name="cancellationToken"/> fired, while the backend call
    /// itself keeps running in the background and is cached for the next search.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="term">The search term.</param>
    /// <param name="user">The calling user, or <c>null</c> for an API-key search.</param>
    /// <param name="kinds">Item kinds allowed (subset of Movie, Series, Episode).</param>
    /// <param name="parentId">Optional subtree to restrict to.</param>
    /// <param name="take">How many matches to return.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The matches, or an empty result when retrieval is not usable.</returns>
    public async Task<SemanticMatchResult> FindAsync(
        PluginConfiguration config,
        string term,
        User? user,
        IReadOnlyCollection<BaseItemKind> kinds,
        Guid? parentId,
        int take,
        CancellationToken cancellationToken)
    {
        var result = new SemanticMatchResult();
        var isRemote = string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase);
        result.Mode = isRemote ? "remote" : "local";
        if (kinds.Count == 0 || string.IsNullOrWhiteSpace(term))
        {
            return result;
        }

        var overfetch = take * 4;
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<(Guid ItemId, float Score)>? ranked;
        if (isRemote)
        {
            var (hits, cached) = await RemoteRetrieveAsync(config, term.Trim(), kinds, overfetch, user, cancellationToken).ConfigureAwait(false);
            ranked = hits;
            result.Cached = cached;
            result.BackendMs = cached ? 0 : stopwatch.ElapsedMilliseconds;
        }
        else
        {
            var local = await _retriever.RetrieveAsync(config, term, overfetch, null, cancellationToken).ConfigureAwait(false);
            ranked = local?.Hits;
            if (local is not null)
            {
                result.BackendMs = local.EmbedMs;
                result.ScanMs = local.ScanMs;
                result.Cached = local.CachedQuery;
            }
        }

        _warmer.MarkUsed();

        // An index built before a library was excluded, or a remote service,
        // can still return its titles: drop them here.
        var excluded = _scope.ExcludedItemIds(config);
        if (ranked is not null && excluded.Count > 0)
        {
            ranked = ranked.Where(h => !excluded.Contains(h.ItemId)).ToList();
        }

        if (ranked is null || ranked.Count == 0)
        {
            return result;
        }

        result.RawHits = ranked.Count;
        var lookupStarted = stopwatch.ElapsedMilliseconds;
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ItemIds = ranked.Select(h => h.ItemId).ToArray(),
            IncludeItemTypes = kinds.ToArray(),
            IsVirtualItem = false,
            Recursive = true,
            AncestorIds = parentId is Guid pid && pid != Guid.Empty ? new[] { pid } : Array.Empty<Guid>(),
            DtoOptions = new DtoOptions(false)
        });

        var byId = new Dictionary<Guid, BaseItem>(items.Count);
        foreach (var item in items)
        {
            byId.TryAdd(item.Id, item);
        }

        var resultIds = new HashSet<Guid>(byId.Keys);
        foreach (var (id, score) in ranked)
        {
            if (!byId.TryGetValue(id, out var item))
            {
                continue;
            }

            // Loading by id skips the library-access part of the user's query,
            // so check visibility and parental rules item by item.
            if (user is not null && !item.IsVisibleStandalone(user))
            {
                continue;
            }

            // Alternate versions of the same title share one primary; skip a
            // version whose primary already made the cut.
            if (item is Video video && video.PrimaryVersionId is Guid primaryId && resultIds.Contains(primaryId))
            {
                continue;
            }

            result.Matches.Add(new SemanticMatch(item, score, TitleMatcher.IsTitleMatch(item, term)));
            if (result.Matches.Count >= take)
            {
                break;
            }
        }

        result.LookupMs = stopwatch.ElapsedMilliseconds - lookupStarted;
        return result;
    }

    private async Task<(List<(Guid ItemId, float Score)>? Hits, bool Cached)> RemoteRetrieveAsync(
        PluginConfiguration config,
        string term,
        IReadOnlyCollection<BaseItemKind> kinds,
        int take,
        User? user,
        CancellationToken cancellationToken)
    {
        var scopes = new List<string>();
        if (kinds.Contains(BaseItemKind.Movie))
        {
            scopes.Add("movies");
        }

        if (kinds.Contains(BaseItemKind.Series) || kinds.Contains(BaseItemKind.Episode))
        {
            scopes.Add("tv");
        }

        if (scopes.Count == 0)
        {
            return (null, false);
        }

        var perScope = await Task.WhenAll(scopes.Select(scope => RemoteScopeAsync(config, term, scope, take, user, cancellationToken))).ConfigureAwait(false);
        if (perScope.All(r => r.Results is null))
        {
            throw new InvalidOperationException(
                perScope.Select(r => r.Error).FirstOrDefault(e => e is not null) ?? "remote search returned no results.");
        }

        // Merge by best score per item id, keeping first-seen order for ties so a
        // single-scope call (the common case) preserves the endpoint's own order.
        var order = new List<Guid>();
        var best = new Dictionary<Guid, float>();
        foreach (var scoped in perScope)
        {
            if (scoped.Results is null)
            {
                continue;
            }

            foreach (var (id, score) in scoped.Results)
            {
                if (!best.TryGetValue(id, out var existing))
                {
                    order.Add(id);
                    best[id] = score;
                }
                else if (score > existing)
                {
                    best[id] = score;
                }
            }
        }

        var merged = order.Select(id => (ItemId: id, Score: best[id])).OrderByDescending(h => h.Score).ToList();
        return (merged, perScope.All(r => r.Cached));
    }

    private async Task<(List<(Guid ItemId, float Score)>? Results, string? Error, bool Cached)> RemoteScopeAsync(
        PluginConfiguration config,
        string term,
        string scope,
        int take,
        User? user,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["query"] = term,
            ["scope"] = scope,
            ["maxResults"] = take,
            ["includeWatched"] = true,
            ["excludeItemIds"] = Array.Empty<string>(),
            ["client"] = new { name = "jellyfin-ai-search", version = ClientVersion }
        };

        // The endpoint expects an object or nothing: a search made with an API
        // key has no user, and sending "user": null gets the call rejected.
        if (user is not null)
        {
            payload["user"] = new { id = user.Id.ToString("N"), name = user.Username ?? string.Empty };
        }

        var key = string.Join('\n', config.SearchEndpointUrl, scope, take.ToString(CultureInfo.InvariantCulture), user?.Id.ToString("N") ?? "-", term);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120));
        try
        {
            var (results, cached) = await RemoteCache.GetAsync(
                key,
                async token =>
                {
                    var response = await _remoteSearchClient
                        .SearchAsync(config.SearchEndpointUrl, config.SearchApiKey, config.TimeoutSeconds, payload, token)
                        .ConfigureAwait(false);
                    return response.Results ?? throw new InvalidOperationException(response.Error ?? "remote search failed.");
                },
                timeout,
                cancellationToken).ConfigureAwait(false);
            return (results, null, cached);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message, false);
        }
    }
}
