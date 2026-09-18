using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Contributes meaning-based matches to Jellyfin's own search (<c>/Items</c>,
/// <c>/Search/Hints</c>), alongside the built-in <c>SqlSearchProvider</c> title
/// search. Discovered and constructed by the server's DI container purely by
/// implementing <see cref="IInternalSearchProvider"/>; an internal provider
/// (never external) so it adds to title matches instead of replacing them, and
/// is trusted to apply its own per-user visibility filtering, which is exactly
/// what this class does before returning anything.
/// </summary>
public sealed class SemanticSearchProvider : IInternalSearchProvider
{
    private static readonly BaseItemKind[] SupportedKinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };
    private static readonly TimeSpan RetrievalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarnInterval = TimeSpan.FromMinutes(1);

    private readonly SemanticRetriever _retriever;
    private readonly RemoteSearchClient _remoteSearchClient;
    private readonly VectorIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<SemanticSearchProvider> _logger;

    private static long _lastWarnTicks;

    /// <summary>Initializes a new instance of the <see cref="SemanticSearchProvider"/> class.</summary>
    /// <param name="retriever">The local (embedding index) retrieval path.</param>
    /// <param name="remoteSearchClient">The remote search endpoint client.</param>
    /// <param name="index">The in-memory local index, to check readiness without running a query.</param>
    /// <param name="libraryManager">Library manager, for loading and visibility-checking candidate items.</param>
    /// <param name="userManager">User manager, for resolving the calling user.</param>
    /// <param name="httpContextAccessor">Gives access to the current request, to store the per-request trace.</param>
    /// <param name="logger">Logger.</param>
    public SemanticSearchProvider(
        SemanticRetriever retriever,
        RemoteSearchClient remoteSearchClient,
        VectorIndex index,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SemanticSearchProvider> logger)
    {
        _retriever = retriever;
        _remoteSearchClient = remoteSearchClient;
        _index = index;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AI Search (semantic)";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 50;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string ClientVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query)
    {
        var trace = GetOrCreateTrace();
        var config = Config;
        var kinds = AllowedKinds(query);

        if (!config.Enabled || !config.SemanticSearchEnabled)
        {
            RecordSkip(trace, "disabled", enabled: false);
            return false;
        }

        var isRemote = string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase);
        if (isRemote)
        {
            if (string.IsNullOrWhiteSpace(config.SearchEndpointUrl))
            {
                RecordSkip(trace, "not_configured", enabled: true);
                return false;
            }
        }
        else
        {
            var target = EmbeddingsTarget.FromConfiguration(config);
            if (target is null)
            {
                RecordSkip(trace, "not_configured", enabled: true);
                return false;
            }

            if (!_index.IsUsable(target.Model))
            {
                RecordSkip(trace, "index_not_ready", enabled: true);
                return false;
            }
        }

        if (kinds.Count == 0)
        {
            RecordSkip(trace, "types_not_indexed", enabled: true);
            return false;
        }

        if (config.SemanticSearchSentencesOnly)
        {
            var wordCount = query.SearchTerm.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount < Math.Max(1, config.SemanticSearchMinWords))
            {
                RecordSkip(trace, "too_short", enabled: true);
                return false;
            }
        }

        if (trace is not null)
        {
            trace.Enabled = true;
            trace.SkippedReason = null;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var trace = GetOrCreateTrace();
        if (trace is not null)
        {
            trace.Ran = true;
        }

        var config = Config;
        var kinds = AllowedKinds(query);
        var take = Math.Clamp(config.SemanticSearchMaxResults, 1, 100);
        var overfetch = take * 4;

        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(RetrievalTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            // Guid.Empty is never a resolvable user; GetUserById throws on it.
            var user = query.UserId is Guid uid && uid != Guid.Empty ? _userManager.GetUserById(uid) : null;
            var isRemote = string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<(Guid ItemId, float Score)>? ranked = isRemote
                ? await RemoteRetrieveAsync(config, query.SearchTerm, kinds, overfetch, user?.Id, user?.Username, linkedSource.Token).ConfigureAwait(false)
                : await _retriever.TryRetrieveScoredAsync(config, query.SearchTerm, overfetch, null, linkedSource.Token, throwOnError: true).ConfigureAwait(false);

            if (trace is not null)
            {
                trace.Mode = isRemote ? "remote" : "local";
            }

            if (ranked is null || ranked.Count == 0)
            {
                FinishTrace(trace, stopwatch, rawHits: 0, matches: 0);
                return Array.Empty<SearchResult>();
            }

            if (trace is not null)
            {
                trace.RawHits = ranked.Count;
            }

            var candidateIds = ranked.Select(h => h.ItemId).ToArray();
            var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = candidateIds,
                IncludeItemTypes = kinds.ToArray(),
                IsVirtualItem = false,
                Recursive = true,
                AncestorIds = query.ParentId is Guid parentId ? new[] { parentId } : Array.Empty<Guid>(),
                DtoOptions = new DtoOptions(false)
            });

            var byId = new Dictionary<Guid, BaseItem>(items.Count);
            foreach (var item in items)
            {
                byId.TryAdd(item.Id, item);
            }

            var resultIds = new HashSet<Guid>(byId.Keys);
            var filtered = new List<(Guid ItemId, float Score)>(Math.Min(ranked.Count, take));
            foreach (var (id, score) in ranked)
            {
                if (!byId.TryGetValue(id, out var item))
                {
                    continue;
                }

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

                filtered.Add((id, score));
                if (filtered.Count >= take)
                {
                    break;
                }
            }

            var count = filtered.Count;
            var output = new List<SearchResult>(count);
            for (var rank = 0; rank < count; rank++)
            {
                var (id, similarity) = filtered[rank];

                // Kept strictly below 50 (SqlSearchProvider's lowest tier, "contains")
                // so semantic hits never outrank a title match.
                var mappedScore = 1f + (48f * (count - rank) / count);
                output.Add(new SearchResult(id, mappedScore));
                if (trace is not null)
                {
                    trace.Hits[id] = (similarity, rank + 1);
                }
            }

            FinishTrace(trace, stopwatch, rawHits: ranked.Count, matches: count);
            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var isTimeout = ex is OperationCanceledException;
            var reason = isTimeout ? "timeout" : ex.Message;
            WarnRateLimited(reason);
            if (trace is not null)
            {
                trace.Error = reason;
                trace.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return Array.Empty<SearchResult>();
        }
    }

    // Both scopes only ever return Movie, Series or Episode, so the allowed set
    // starts there and is narrowed by the query's own type/media constraints.
    private static HashSet<BaseItemKind> AllowedKinds(SearchProviderQuery query)
    {
        var kinds = new HashSet<BaseItemKind>(SupportedKinds);
        if (query.IncludeItemTypes.Length > 0)
        {
            kinds.IntersectWith(query.IncludeItemTypes);
        }
        else if (query.ExcludeItemTypes.Length > 0)
        {
            kinds.ExceptWith(query.ExcludeItemTypes);
        }

        if (query.MediaTypes.Length > 0)
        {
            if (Array.IndexOf(query.MediaTypes, MediaType.Video) >= 0)
            {
                kinds.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Episode });
            }
            else
            {
                // A non-video media type was requested; nothing we index qualifies
                // (a Series itself carries no media type).
                kinds.Clear();
            }
        }

        return kinds;
    }

    private async Task<List<(Guid ItemId, float Score)>?> RemoteRetrieveAsync(
        PluginConfiguration config,
        string term,
        HashSet<BaseItemKind> kinds,
        int take,
        Guid? userId,
        string? userName,
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
            return null;
        }

        var perScope = await Task.WhenAll(
            scopes.Select(scope => RemoteScopeAsync(config, term, scope, take, userId, userName, cancellationToken)))
            .ConfigureAwait(false);

        if (perScope.All(r => r.Results is null))
        {
            // Every scope failed (or none are configured to return anything):
            // throw so the caller's single catch block records the error in the
            // trace and rate-limits its own warning, the same path a local-index
            // failure takes. A genuine cancellation already propagated out of
            // Task.WhenAll above rather than reaching here.
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

        return order
            .Select(id => (ItemId: id, Score: best[id]))
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    private Task<RemoteSearchResponse> RemoteScopeAsync(
        PluginConfiguration config,
        string term,
        string scope,
        int take,
        Guid? userId,
        string? userName,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            query = term,
            scope,
            maxResults = take,
            includeWatched = true,
            excludeItemIds = Array.Empty<string>(),
            user = userId is Guid uid ? new { id = uid.ToString("N"), name = userName ?? string.Empty } : null,
            client = new { name = "jellyfin-ai-search", version = ClientVersion }
        };

        return _remoteSearchClient.SearchAsync(config.SearchEndpointUrl, config.SearchApiKey, config.TimeoutSeconds, payload, cancellationToken);
    }

    private SemanticSearchTrace? GetOrCreateTrace()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        if (context.Items[SemanticSearchTrace.HttpContextKey] is SemanticSearchTrace existing)
        {
            return existing;
        }

        var trace = new SemanticSearchTrace();
        context.Items[SemanticSearchTrace.HttpContextKey] = trace;
        return trace;
    }

    private static void RecordSkip(SemanticSearchTrace? trace, string reason, bool enabled)
    {
        if (trace is null)
        {
            return;
        }

        trace.Enabled = enabled;
        trace.Ran = false;
        trace.SkippedReason = reason;
    }

    private static void FinishTrace(SemanticSearchTrace? trace, Stopwatch stopwatch, int rawHits, int matches)
    {
        if (trace is null)
        {
            return;
        }

        trace.RawHits = rawHits;
        trace.Matches = matches;
        trace.DurationMs = stopwatch.ElapsedMilliseconds;
    }

    private void WarnRateLimited(string reason)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarnTicks);
        if (now - last < WarnInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastWarnTicks, now, last) == last)
        {
            _logger.LogWarning("AiSearch: native-search semantic retrieval failed ({Reason}).", reason);
        }
    }
}
