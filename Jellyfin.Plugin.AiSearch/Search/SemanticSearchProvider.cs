using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
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
/// is trusted to apply its own per-user visibility filtering, which
/// <see cref="SemanticMatcher"/> does before anything is returned.
/// <para>
/// Jellyfin answers a search only once every provider has, so anything this
/// provider adds delays the title results too. The web client with the
/// plugin's script therefore marks its search requests, and this provider
/// steps aside for them: the script shows meaning matches in their own row,
/// fetched separately, after Jellyfin's results are already on screen. Other
/// clients (TV apps, mobile apps, API callers) still get the matches merged in,
/// within a capped wait.
/// </para>
/// </summary>
public sealed class SemanticSearchProvider : IInternalSearchProvider
{
    /// <summary>Request header the web client script sets on its search requests.</summary>
    public const string ClientRowHeader = "X-AiSearch-Row";

    private static readonly BaseItemKind[] SupportedKinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };
    private static readonly TimeSpan WarnInterval = TimeSpan.FromMinutes(1);

    private readonly SemanticMatcher _matcher;
    private readonly VectorIndex _index;
    private readonly IUserManager _userManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SemanticWarmer _warmer;
    private readonly ILogger<SemanticSearchProvider> _logger;

    private static long _lastWarnTicks;

    /// <summary>Initializes a new instance of the <see cref="SemanticSearchProvider"/> class.</summary>
    /// <param name="matcher">Finds the visible meaning-based matches.</param>
    /// <param name="index">The in-memory local index, to check readiness without running a query.</param>
    /// <param name="userManager">User manager, for resolving the calling user.</param>
    /// <param name="httpContextAccessor">Gives access to the current request, to store the per-request trace.</param>
    /// <param name="warmer">Keeps the retrieval backend loaded between searches.</param>
    /// <param name="logger">Logger.</param>
    public SemanticSearchProvider(
        SemanticMatcher matcher,
        VectorIndex index,
        IUserManager userManager,
        IHttpContextAccessor httpContextAccessor,
        SemanticWarmer warmer,
        ILogger<SemanticSearchProvider> logger)
    {
        _matcher = matcher;
        _index = index;
        _userManager = userManager;
        _httpContextAccessor = httpContextAccessor;
        _warmer = warmer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AI Search (semantic)";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 50;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Whether semantic matching can run at all with this configuration, and
    /// why not when it cannot. Shared with the web client's AI Search row.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="index">The local index.</param>
    /// <returns><c>null</c> when usable, otherwise a short skip reason.</returns>
    public static string? Unavailable(PluginConfiguration config, VectorIndex index)
    {
        if (!config.Enabled || !config.SemanticSearchEnabled)
        {
            return "disabled";
        }

        if (string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(config.SearchEndpointUrl) ? "not_configured" : null;
        }

        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null)
        {
            return "not_configured";
        }

        return index.IsUsable(target.Model) ? null : "index_not_ready";
    }

    /// <summary>Whether the term is too short for the "sentences only" option.</summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="term">The search term.</param>
    /// <returns><c>true</c> when semantic matching should skip this term.</returns>
    public static bool TooShort(PluginConfiguration config, string term)
    {
        if (!config.SemanticSearchSentencesOnly)
        {
            return false;
        }

        var wordCount = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount < Math.Max(1, config.SemanticSearchMinWords);
    }

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query)
    {
        var trace = GetOrCreateTrace();
        var config = Config;

        var unavailable = Unavailable(config, _index);
        if (unavailable is not null)
        {
            RecordSkip(trace, unavailable, enabled: unavailable != "disabled");
            return false;
        }

        // The web client shows these matches in its own row, so its title
        // results must not wait for them.
        if (config.ShowAiSearchRow && _httpContextAccessor.HttpContext?.Request.Headers.ContainsKey(ClientRowHeader) == true)
        {
            RecordSkip(trace, "shown_in_ai_row", enabled: true);
            return false;
        }

        if (AllowedKinds(query).Count == 0)
        {
            RecordSkip(trace, "types_not_indexed", enabled: true);
            return false;
        }

        if (TooShort(config, query.SearchTerm))
        {
            // The sentence is still being typed: load a cold model now so it
            // is ready by the time the query is long enough to run.
            _warmer.Touch(config);
            RecordSkip(trace, "too_short", enabled: true);
            return false;
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
        var config = Config;
        var take = Math.Clamp(config.SemanticSearchMaxResults, 1, 100);
        if (trace is not null)
        {
            trace.Ran = true;
            trace.Mode = string.Equals(config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase) ? "remote" : "local";
        }

        // Jellyfin answers a search only once every provider has, so this
        // budget is the most meaning matches may ever add to a search. The
        // backend call itself is not cut short (see CoalescingCache).
        var budget = TimeSpan.FromMilliseconds(Math.Clamp(config.SemanticSearchTimeoutMs, 250, 10000));
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(budget);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            // Guid.Empty is never a resolvable user; GetUserById throws on it.
            var user = query.UserId is Guid uid && uid != Guid.Empty ? _userManager.GetUserById(uid) : null;
            var found = await _matcher
                .FindAsync(config, query.SearchTerm, user, AllowedKinds(query), query.ParentId, take, linkedSource.Token)
                .ConfigureAwait(false);

            var count = found.Matches.Count;
            var output = new List<SearchResult>(count);
            for (var rank = 0; rank < count; rank++)
            {
                var match = found.Matches[rank];

                // Kept strictly below 50 (SqlSearchProvider's lowest tier, "contains")
                // so semantic hits never outrank a title match.
                output.Add(new SearchResult(match.Item.Id, 1f + (48f * (count - rank) / count)));
                trace?.Hits.TryAdd(match.Item.Id, new SemanticHit(match.Similarity, rank + 1, match.TitleMatch));
            }

            if (trace is not null)
            {
                trace.Mode = found.Mode;
                trace.BackendMs = found.BackendMs;
                trace.ScanMs = found.ScanMs;
                trace.LookupMs = found.LookupMs;
                trace.Cached = found.Cached;
                trace.RawHits = found.RawHits;
                trace.Matches = count;
                trace.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var isTimeout = ex is OperationCanceledException or TimeoutException;
            if (!isTimeout)
            {
                WarnRateLimited(ex.Message);
            }

            if (trace is not null)
            {
                trace.Error = isTimeout ? "timeout" : ex.Message;
                trace.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// The kinds a query allows: Movie, Series or Episode, narrowed by the
    /// query's own type and media constraints.
    /// </summary>
    /// <param name="query">The provider query.</param>
    /// <returns>The allowed kinds, possibly empty.</returns>
    internal static HashSet<BaseItemKind> AllowedKinds(SearchProviderQuery query) =>
        AllowedKinds(query.IncludeItemTypes, query.ExcludeItemTypes, query.MediaTypes);

    /// <summary>Narrows Movie, Series and Episode by include/exclude types and media types.</summary>
    /// <param name="include">Included kinds (empty = all).</param>
    /// <param name="exclude">Excluded kinds, used when nothing is included explicitly.</param>
    /// <param name="mediaTypes">Requested media types (empty = any).</param>
    /// <returns>The allowed kinds, possibly empty.</returns>
    internal static HashSet<BaseItemKind> AllowedKinds(IReadOnlyCollection<BaseItemKind> include, IReadOnlyCollection<BaseItemKind> exclude, IReadOnlyCollection<MediaType> mediaTypes)
    {
        var kinds = new HashSet<BaseItemKind>(SupportedKinds);
        if (include.Count > 0)
        {
            kinds.IntersectWith(include);
        }
        else if (exclude.Count > 0)
        {
            kinds.ExceptWith(exclude);
        }

        if (mediaTypes.Count > 0)
        {
            if (System.Linq.Enumerable.Contains(mediaTypes, MediaType.Video))
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
