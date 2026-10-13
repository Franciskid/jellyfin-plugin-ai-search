using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using Jellyfin.Plugin.AiSearch.SearchLog;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>Public request body for a recommendation.</summary>
public class RecommendRequest
{
    /// <summary>Gets or sets the natural-language prompt (optional in surprise mode).</summary>
    public string? Prompt { get; set; }

    /// <summary>Gets or sets an optional per-request result cap.</summary>
    public int? MaxResults { get; set; }

    /// <summary>Gets or sets the UI language ("fr" or "en") to answer in.</summary>
    public string? Locale { get; set; }

    /// <summary>Gets or sets the mode: "normal" (default) or "surprise".</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the search scope: "movies" (default) or "tv".</summary>
    public string? Scope { get; set; }

    /// <summary>Gets or sets a per-request override for including already-watched movies.</summary>
    public bool? IncludeWatched { get; set; }

    /// <summary>
    /// Gets or sets whether to flavor results with the user's taste (favorites,
    /// watch history, taste profile). Default true; false = a neutral search.
    /// </summary>
    public bool? Personalize { get; set; }

    /// <summary>Gets or sets item ids to exclude, used by "show more" to avoid repeats.</summary>
    public string[]? ExcludeItemIds { get; set; }
}

/// <summary>Public request body for adaptive "Help me choose" questions.</summary>
public class InterviewRequest
{
    /// <summary>Gets or sets the user's initial request to tailor the questions to.</summary>
    public string? Prompt { get; set; }

    /// <summary>Gets or sets the UI language ("fr" or "en").</summary>
    public string? Locale { get; set; }
}

/// <summary>Public request body to save a set of movies as a per-user playlist.</summary>
public class PlaylistRequest
{
    /// <summary>Gets or sets the playlist name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the item ids to add.</summary>
    public string[]? ItemIds { get; set; }
}

/// <summary>
/// Authenticates the caller and returns recommendations. Retrieval is either
/// remote (a search endpoint returns candidates) or local (the plugin's own
/// semantic index, falling back to catalog selection when none is built). Either
/// way the chosen candidates go to an OpenAI-compatible chat endpoint that picks
/// and explains the results.
/// </summary>
[ApiController]
[Route("AiSearch")]
public class AiSearchController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemanticRetriever _semanticRetriever;
    private readonly SemanticWarmer _warmer;
    private readonly LibraryScope _scope;
    private readonly RemoteSearchClient _remoteSearchClient;
    private readonly DirectChatClient _directChat;
    private readonly IPlaylistManager _playlistManager;
    private readonly HistoryStore _history;
    private readonly TasteProfileStore _taste;
    private readonly SearchLogStore _searchLog;
    private readonly ILogger<AiSearchController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AiSearchController"/> class.</summary>
    public AiSearchController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IHttpClientFactory httpClientFactory,
        SemanticRetriever semanticRetriever,
        SemanticWarmer warmer,
        LibraryScope scope,
        RemoteSearchClient remoteSearchClient,
        DirectChatClient directChat,
        IPlaylistManager playlistManager,
        HistoryStore history,
        TasteProfileStore taste,
        SearchLogStore searchLog,
        ILogger<AiSearchController> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _httpClientFactory = httpClientFactory;
        _semanticRetriever = semanticRetriever;
        _warmer = warmer;
        _scope = scope;
        _remoteSearchClient = remoteSearchClient;
        _directChat = directChat;
        _playlistManager = playlistManager;
        _history = history;
        _taste = taste;
        _searchLog = searchLog;
        _logger = logger;
    }

    // Synthetic request for surprise mode, the model still personalizes via the
    // user's favorites/watched signals, but is steered toward variety.
    private const string SurprisePrompt =
        "Surprise me. From the candidates, pick a delightfully varied, unexpected mix I might enjoy " +
        "but would not think to search for. Favor variety across genres and eras over obvious blockbusters.";

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static bool IsRemoteRetrieval =>
        string.Equals(Config.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase);

    private static string ClientVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    // Configured needs a chat endpoint (unless the model is disabled) plus, for
    // remote retrieval, a search endpoint. Local retrieval always has the catalog
    // fallback, so it needs nothing beyond the above.
    private static bool Configured(PluginConfiguration c) =>
        (!c.UseModel || !string.IsNullOrWhiteSpace(c.ChatEndpointUrl))
        && (!string.Equals(c.RetrievalMode, "remote", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(c.SearchEndpointUrl));

    /// <summary>Serves the injected web-client script (no auth; contains no secrets).</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        // The injected <script> tag is unversioned, so tell browsers to revalidate;
        // otherwise a stale cached copy survives plugin updates.
        Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.AiSearch.Web.ai-search.js");
        return stream is null ? NotFound() : File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Lightweight status endpoint so the client knows which of its features
    /// apply: the AI popup (hidden in native-search-only mode) and the AI
    /// Search row on Jellyfin's search page.
    /// </summary>
    [HttpGet("Health")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult Health()
    {
        var c = Config;
        return Ok(new
        {
            enabled = c.Enabled,
            configured = Configured(c),
            popup = !c.NativeSearchOnly,
            language = string.IsNullOrWhiteSpace(c.UiLanguage) ? "auto" : c.UiLanguage.Trim().ToLowerInvariant(),
            aiRow = c.Enabled && c.SemanticSearchEnabled && c.ShowAiSearchRow,
            explain = c.ExplainAiMatches && c.UseModel && !string.IsNullOrWhiteSpace(c.ChatEndpointUrl),
            mode = IsRemoteRetrieval ? "remote" : "local",
            model = c.Model,
            maxResults = c.MaxResults
        });
    }

    /// <summary>
    /// Loads a cold retrieval backend ahead of the user's query. The client
    /// calls it when the search page or the AI popup opens; it returns at once
    /// and does nothing when the backend was used in the last few minutes.
    /// </summary>
    [HttpPost("Warmup")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult Warmup()
    {
        return Ok(new { started = _warmer.Touch(Config) });
    }

    /// <summary>Proxies the model-alias list for the admin config dropdown (admin only; only the config page calls this).</summary>
    [HttpGet("Models")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public async Task<ActionResult> Models(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var c = Config;
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        return await ListModelsAsync(c.ModelsEndpointUrl, c.ChatApiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Proxies the embedding-model list for the local-index dropdown (admin only; only the config page calls this).</summary>
    [HttpGet("EmbeddingModels")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> EmbeddingModels(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        var c = Config;
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        // A dedicated embedding-models URL when set, otherwise the main models
        // list (the user picks the embedding model from all of them).
        var url = string.IsNullOrWhiteSpace(c.EmbeddingModelsEndpointUrl) ? c.ModelsEndpointUrl : c.EmbeddingModelsEndpointUrl;
        var key = string.IsNullOrWhiteSpace(c.EmbeddingApiKey) ? c.ChatApiKey : c.EmbeddingApiKey;
        return await ListModelsAsync(url, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult> ListModelsAsync(string endpointUrl, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            // No listing endpoint: the dashboard just keeps the typed model id.
            return Ok(new { data = Array.Empty<object>() });
        }

        try
        {
            using var client = CreateClient(Config, apiKey);
            using var resp = await client.GetAsync(endpointUrl.Trim(), cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AiSearch: models endpoint returned {Status}: {Body}", resp.StatusCode, body);
                return Ok(new { data = Array.Empty<object>(), error = $"{(int)resp.StatusCode} {resp.StatusCode}: {body}" });
            }

            var ids = ExtractModelIds(body);
            return Ok(new { data = ids.Select(id => new { id }) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: failed to list models.");
            return Ok(new { data = Array.Empty<object>(), error = ex.Message });
        }
    }

    /// <summary>
    /// Normalizes the various shapes providers use for a models listing
    /// (OpenAI's {data:[{id}]}, a bare array, or Ollama-style {models:[{name|model}]})
    /// into a flat list of model id strings.
    /// </summary>
    private static List<string> ExtractModelIds(string body)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            JsonValueKind.Object when root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array => models,
            _ => default
        };

        if (items.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                ids.Add(item.GetString()!);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = TryGetString(item, "id") ?? TryGetString(item, "name") ?? TryGetString(item, "model");
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string? TryGetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Returns AI-chosen library recommendations for the authenticated user's prompt.</summary>
    [HttpPost("Recommend")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public async Task<ActionResult> Recommend([FromBody] RecommendRequest request, CancellationToken cancellationToken)
    {
        var requestStartedAt = DateTime.UtcNow;
        var requestStopwatch = Stopwatch.StartNew();
        var c = Config;
        if (!c.Enabled)
        {
            return StatusCode(503, new { error = "AI search is disabled." });
        }

        if (!Configured(c))
        {
            return StatusCode(503, new { error = "AI search is not configured." });
        }

        if (request is null)
        {
            return BadRequest(new { error = "Missing request." });
        }

        var isSurprise = string.Equals(request.Mode, "surprise", StringComparison.OrdinalIgnoreCase);
        if (!isSurprise && string.IsNullOrWhiteSpace(request.Prompt))
        {
            return BadRequest(new { error = "Missing prompt." });
        }

        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        var maxResults = request.MaxResults is > 0 and <= 24 ? request.MaxResults.Value : c.MaxResults;
        var locale = c.ResolveLocale(request.Locale);
        var includeWatched = request.IncludeWatched ?? c.IncludeWatched;
        var personalize = request.Personalize ?? true;
        var isTv = string.Equals(request.Scope, "tv", StringComparison.OrdinalIgnoreCase);
        var exclude = ParseGuids(request.ExcludeItemIds);

        // "Show more" pagination sends the already-shown ids; only the first
        // page of a search is worth recording as a history entry.
        var record = exclude.Count == 0;
        var mode = isSurprise ? "surprise" : "normal";
        var historyPrompt = isSurprise ? string.Empty : request.Prompt!.Trim();
        var modelPrompt = isSurprise ? SurprisePrompt : historyPrompt;

        // A stable id tying an initial search to its "see more" follow-ups into one
        // conversation. The first page starts a new id; "see more" reuses the latest
        // matching history entry's id (each entry == one conversation). Sent to the
        // platform so its usage history can group the turns as a single thread.
        var conversationId = record
            ? Guid.NewGuid().ToString("N")
            : (_history.Load(userId).FirstOrDefault(e =>
                   string.Equals(e.Mode, mode, StringComparison.Ordinal) &&
                   string.Equals(e.Prompt, historyPrompt, StringComparison.Ordinal))?.Id
               ?? Guid.NewGuid().ToString("N"));

        // Retrieval first: the semantic paths only need the user's view of the
        // few dozen ids they return, not the whole library. Loading every item
        // of the scope with its user data (thousands of rows once episodes are
        // indexed) is left to the paths that actually pick from the full pool:
        // surprise mode and the catalog fallback.
        var scopeKinds = isTv
            ? new[] { BaseItemKind.Series, BaseItemKind.Episode }
            : new[] { BaseItemKind.Movie };

        List<BaseItem>? candidates = null;
        var usedSemantic = false;
        if (!isSurprise)
        {
            // Retrieval source: a remote search endpoint, or the local semantic
            // index. Either returns candidate items; both fall back to a catalog
            // slice when unavailable (not configured, or the call/embedding fails).
            candidates = IsRemoteRetrieval
                ? await RemoteSearchAsync(c, modelPrompt, user, scopeKinds, includeWatched, exclude, isTv, userId, user.Username, cancellationToken).ConfigureAwait(false)
                : await SemanticCandidatesAsync(c, modelPrompt, user, scopeKinds, includeWatched, exclude, isTv, cancellationToken).ConfigureAwait(false);
            usedSemantic = candidates is not null;
            if (usedSemantic)
            {
                _warmer.MarkUsed();
            }
        }

        LibraryView? view = null;
        if (candidates is null)
        {
            view = LoadLibraryView(user, scopeKinds, includeWatched, exclude, personalize);
            if (isSurprise)
            {
                // Surprise mode skips the semantic index (there is no query to
                // match) and hands the model a broad random slice. For TV scope,
                // restrict to series: surprising the user with a single episode
                // rather than a show to watch isn't the point of the feature.
                var surprisePool = isTv ? view.Pool.Where(item => item is Series).ToList() : view.Pool;
                if (isTv && surprisePool.Count == 0)
                {
                    surprisePool = view.All.Where(item => item is Series && !exclude.Contains(item.Id)).ToList();
                }

                candidates = CandidateSelector.Select(surprisePool, "random", Math.Max(10, c.MaxCatalogItems));
            }
            else
            {
                candidates = CandidateSelector.Select(view.Pool, c.SelectionStrategy, Math.Max(10, c.MaxCatalogItems));
            }
        }

        // Model disabled: return the retrieval matches directly, ranked as
        // retrieved, with no model call and no per-result reason.
        if (!c.UseModel)
        {
            return RetrievalOnly(candidates, maxResults, userId, historyPrompt, mode, record, conversationId, requestStartedAt, requestStopwatch);
        }

        // Resolve cast from live metadata only on the semantic path (≤ ~40
        // candidates); the fallback dump would mean hundreds of people lookups.
        var enriched = candidates
            .Select(item => new CandidateMovie(
                item,
                usedSemantic ? CastFormatter.Format(_libraryManager.GetPeople(item)) : string.Empty))
            .ToList();

        // Taste signals: already gathered by the full pass when one ran,
        // otherwise two small, capped queries.
        var favorites = new List<string>();
        var watched = new List<string>();
        if (personalize)
        {
            if (view is not null)
            {
                favorites = view.Favorites;
                watched = view.Watched;
            }
            else
            {
                (favorites, watched) = LoadTasteSignals(user, scopeKinds);
            }
        }

        // Distilled taste summary (silent): fed into the prompt when personalizing,
        // and lazily (re)built in the background as the user's taste drifts.
        var profileText = personalize ? _taste.Load(userId)?.Text : null;
        if (personalize)
        {
            MaybeRefreshProfile(c, userId, favorites, watched, locale);
        }

        return await RunRecommendation(c, modelPrompt, locale, maxResults, enriched, favorites, watched, usedSemantic, userId, historyPrompt, mode, record, conversationId, profileText, requestStartedAt, requestStopwatch, cancellationToken).ConfigureAwait(false);
    }

    // Regenerates the taste profile in the background when it is missing, older
    // than a week, or the user's favorite/watched signal has drifted enough.
    private void MaybeRefreshProfile(PluginConfiguration c, Guid userId, List<string> favorites, List<string> watched, string locale)
    {
        var signal = favorites.Count + watched.Count;
        if (signal < 3)
        {
            return;
        }

        var existing = _taste.Load(userId);
        if (existing is not null
            && DateTime.TryParse(existing.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var built)
            && (DateTime.UtcNow - built).TotalDays < 7
            && Math.Abs((existing.FavCount + existing.WatchedCount) - signal) < 8)
        {
            return;
        }

        var favs = new List<string>(favorites);
        var seen = new List<string>(watched);
        _ = Task.Run(async () =>
        {
            try
            {
                var messages = PromptBuilder.BuildTasteProfileMessages(favs, seen, locale);
                var text = CleanProfileText(await _directChat.CompleteAsync(c, c.ChatEndpointUrl, c.ChatApiKey, c.Model, messages, CancellationToken.None).ConfigureAwait(false) ?? string.Empty);
                if (text.Length > 0)
                {
                    _taste.Save(userId, new TasteProfile
                    {
                        Text = text,
                        BuiltAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        FavCount = favs.Count,
                        WatchedCount = seen.Count
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AiSearch: taste profile refresh failed.");
            }
        });
    }

    // One pass over the user's whole view of the scope: the watch-filtered
    // pool the catalog fallback and surprise mode pick from, plus taste
    // signals. Only those paths need it; semantic retrieval does not.
    private LibraryView LoadLibraryView(Jellyfin.Database.Implementations.Entities.User user, BaseItemKind[] scopeKinds, bool includeWatched, HashSet<Guid> exclude, bool personalize)
    {
        var all = _scope.Filter(Config, _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = scopeKinds,
            Recursive = true,
            IsVirtualItem = false
        }));

        var view = new LibraryView(all);
        foreach (var item in all)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var ud = _userDataManager.GetUserData(user, item);
            var played = ud?.Played ?? false;
            if (personalize && (ud?.IsFavorite ?? false) && view.Favorites.Count < 60)
            {
                view.Favorites.Add(TitleYear(item));
            }

            if (personalize && played && view.Watched.Count < 80)
            {
                view.Watched.Add(TitleYear(item));
            }

            if ((includeWatched || !played) && !exclude.Contains(item.Id))
            {
                view.Pool.Add(item);
            }
        }

        if (view.Pool.Count == 0)
        {
            view.Pool.AddRange(all.Where(m => !string.IsNullOrWhiteSpace(m.Name) && !exclude.Contains(m.Id)));
        }

        return view;
    }

    // Favorites and the most recently watched titles, for the prompt, without
    // walking the whole library: the database filters and caps both lists.
    private (List<string> Favorites, List<string> Watched) LoadTasteSignals(Jellyfin.Database.Implementations.Entities.User user, BaseItemKind[] scopeKinds)
    {
        var favorites = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = scopeKinds,
            Recursive = true,
            IsVirtualItem = false,
            IsFavorite = true,
            Limit = 60,
            DtoOptions = new DtoOptions(false)
        });
        var watched = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = scopeKinds,
            Recursive = true,
            IsVirtualItem = false,
            IsPlayed = true,
            Limit = 80,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(false)
        });

        return (
            _scope.Filter(Config, favorites).Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(TitleYear).ToList(),
            _scope.Filter(Config, watched).Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(TitleYear).ToList());
    }

    // Maps ranked ids back to items the user may see, in rank order, applying
    // the exclude list and (unless asked otherwise) dropping watched titles.
    // Loading by id skips the library-access part of the user's query, so each
    // item is visibility-checked on its own as well.
    private List<BaseItem>? PickVisible(
        IEnumerable<Guid> rankedIds,
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind[] scopeKinds,
        bool includeWatched,
        HashSet<Guid> exclude,
        int maxRetrieve)
    {
        var excludedLibraries = _scope.ExcludedItemIds(Config);
        var ids = rankedIds.Where(id => !exclude.Contains(id) && !excludedLibraries.Contains(id)).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return null;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            ItemIds = ids,
            IncludeItemTypes = scopeKinds,
            Recursive = true,
            IsVirtualItem = false
        });

        var byId = new Dictionary<Guid, BaseItem>(items.Count);
        foreach (var item in items)
        {
            byId.TryAdd(item.Id, item);
        }

        var picked = new List<BaseItem>(maxRetrieve);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item) || string.IsNullOrWhiteSpace(item.Name) || !item.IsVisibleStandalone(user))
            {
                continue;
            }

            if (!includeWatched && (_userDataManager.GetUserData(user, item)?.Played ?? false))
            {
                continue;
            }

            picked.Add(item);
            if (picked.Count >= maxRetrieve)
            {
                break;
            }
        }

        return picked.Count > 0 ? picked : null;
    }

    // --- Direct mode: semantic retrieval over the plugin's own index. ---
    private async Task<List<BaseItem>?> SemanticCandidatesAsync(
        PluginConfiguration c,
        string prompt,
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind[] scopeKinds,
        bool includeWatched,
        HashSet<Guid> exclude,
        bool isTv,
        CancellationToken cancellationToken)
    {
        var maxRetrieve = Math.Clamp(c.MaxRetrieve, 1, 200);

        // Over-fetch so watched/visibility filtering still leaves enough.
        HashSet<Guid>? allowedIds = null;
        var take = maxRetrieve * 3;
        if (isTv)
        {
            // Episode-dense libraries dilute global ranking; widen the net.
            take = maxRetrieve * 6;

            // If the query names a series we hold, disambiguate within that
            // series' episodes instead of across the whole library.
            var series = DetectSeries(prompt, _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            }));
            if (series is not null)
            {
                allowedIds = new HashSet<Guid>(_libraryManager.GetItemIds(new InternalItemsQuery(user)
                {
                    AncestorIds = new[] { series.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                }))
                {
                    series.Id
                };
                take = Math.Max(maxRetrieve, allowedIds.Count);
            }
        }

        var ids = await _semanticRetriever.TryRetrieveAsync(c, prompt, take, allowedIds, cancellationToken).ConfigureAwait(false);
        if (ids is null)
        {
            return null;
        }

        return PickVisible(ids, user, scopeKinds, includeWatched, exclude, maxRetrieve);
    }

    // Finds the library series whose name appears verbatim (case-insensitive) in
    // the query, preferring the longest such name so "Star Trek: TNG" beats
    // "Star Trek". Returns null when the query names no held series.
    private static BaseItem? DetectSeries(string prompt, IReadOnlyList<BaseItem> seriesList)
    {
        var q = prompt.ToLowerInvariant();
        BaseItem? best = null;
        var bestLen = 0;
        foreach (var item in seriesList)
        {
            if (item is not Series || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length < 3)
            {
                continue;
            }

            if (item.Name.Length > bestLen && q.Contains(item.Name.ToLowerInvariant(), StringComparison.Ordinal))
            {
                best = item;
                bestLen = item.Name.Length;
            }
        }

        return best;
    }

    // Remote retrieval: a search endpoint returns ranked itemIds for the query; we
    // map them back to the user's own library items (enforcing per-user visibility)
    // and let the model pick locally, the same flow as the local index, different
    // source. Returns null on any failure so the caller falls back to a catalog slice.
    private async Task<List<BaseItem>?> RemoteSearchAsync(
        PluginConfiguration c,
        string prompt,
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind[] scopeKinds,
        bool includeWatched,
        HashSet<Guid> exclude,
        bool isTv,
        Guid userId,
        string userName,
        CancellationToken cancellationToken)
    {
        var maxRetrieve = Math.Clamp(c.MaxRetrieve, 1, 200);
        var payload = new
        {
            query = prompt,
            scope = isTv ? "tv" : "movies",
            // Over-fetch so per-user visibility + watched filtering still leave enough.
            maxResults = maxRetrieve * 3,
            includeWatched,
            excludeItemIds = exclude.Select(g => g.ToString("N")).ToArray(),
            user = new { id = userId.ToString("N"), name = userName },
            client = new { name = "jellyfin-ai-search", version = ClientVersion }
        };

        var response = await _remoteSearchClient
            .SearchAsync(c.SearchEndpointUrl, c.SearchApiKey, c.TimeoutSeconds, payload, cancellationToken)
            .ConfigureAwait(false);
        if (response.Error is not null)
        {
            _logger.LogWarning("AiSearch: search endpoint call failed: {Error}", response.Error);
        }

        return response.Results is null
            ? null
            : PickVisible(response.Results.Select(r => r.ItemId), user, scopeKinds, includeWatched, exclude, maxRetrieve);
    }

    // Model disabled: the retrieved candidates, already ranked by relevance, are
    // the results. No model call, no per-result reason, no answer summary.
    private ActionResult RetrievalOnly(List<BaseItem> candidates, int maxResults, Guid userId, string historyPrompt, string mode, bool record, string conversationId, DateTime requestStartedAt, Stopwatch requestStopwatch)
    {
        var items = new List<HistoryItem>();
        foreach (var item in candidates)
        {
            items.Add(new HistoryItem
            {
                ItemId = item.Id.ToString("N"),
                Title = DisplayTitle(item),
                Year = item.ProductionYear,
                Reason = null
            });
            if (items.Count >= maxResults)
            {
                break;
            }
        }

        if (record)
        {
            RecordHistory(userId, conversationId, historyPrompt, mode, string.Empty, items);
        }
        else
        {
            AppendHistory(userId, historyPrompt, mode, items);
        }

        EnqueueAiSearchLog(requestStartedAt, requestStopwatch.ElapsedMilliseconds, historyPrompt, mode, null, items);
        return Ok(new { answer = (string?)null, model = (string?)null, usedProfile = false, results = ToResults(items) });
    }

    // --- The model call: candidate list in, chosen recommendations out. ---
    private async Task<ActionResult> RunRecommendation(
        PluginConfiguration c, string prompt, string locale, int maxResults, List<CandidateMovie> candidates, List<string> favorites, List<string> watched, bool usedSemantic, Guid userId, string historyPrompt, string mode, bool record, string conversationId, string? tasteProfile, DateTime requestStartedAt, Stopwatch requestStopwatch, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Ok(new { answer = "Your movie library looks empty.", model = c.Model, usedProfile = false, results = Array.Empty<object>() });
        }

        // Cast + synopsis only with semantic retrieval: ~40 rich lines help the
        // model judge fit, while hundreds of them would explode the token count.
        var messages = PromptBuilder.BuildMessages(prompt, locale, maxResults, candidates, favorites, watched, includeDetails: usedSemantic, synopsisLength: c.SynopsisMaxLength, tasteProfile: tasteProfile);

        string content;
        try
        {
            content = await _directChat.CompleteAsync(c, c.ChatEndpointUrl, c.ChatApiKey, c.Model, messages, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { error = "AI service timed out." });
        }
        catch (DirectChatException ex)
        {
            _logger.LogWarning("AiSearch: direct model call failed: {Message}", ex.Message);
            return StatusCode(502, new { error = "AI service error." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: direct model call failed.");
            return StatusCode(502, new { error = "AI service unavailable." });
        }

        var parsed = ModelResponseParser.Parse(content);
        if (parsed is null)
        {
            return StatusCode(502, new { error = "Could not understand the AI response." });
        }

        var items = new List<HistoryItem>();
        foreach (var rec in parsed.Recommendations)
        {
            if (rec.Index < 0 || rec.Index >= candidates.Count)
            {
                continue;
            }

            var item = candidates[rec.Index].Item;
            items.Add(new HistoryItem
            {
                ItemId = item.Id.ToString("N"),
                Title = DisplayTitle(item),
                Year = item.ProductionYear,
                Reason = rec.Reason
            });
            if (items.Count >= maxResults)
            {
                break;
            }
        }

        if (record)
        {
            RecordHistory(userId, conversationId, historyPrompt, mode, parsed.Answer ?? string.Empty, items);
        }
        else
        {
            AppendHistory(userId, historyPrompt, mode, items);
        }

        EnqueueAiSearchLog(requestStartedAt, requestStopwatch.ElapsedMilliseconds, historyPrompt, mode, parsed.Answer, items);

        var usedProfile = favorites.Count > 0 || watched.Count > 0 || !string.IsNullOrWhiteSpace(tasteProfile);
        return Ok(new { answer = parsed.Answer, model = c.Model, usedProfile, results = ToResults(items) });
    }

    /// <summary>
    /// Returns a few model-generated multiple-choice questions tailored to the
    /// caller's initial prompt, for the "Help me choose" flow. Degrades to an
    /// empty list (client falls back to its generic questions) when disabled,
    /// nothing is configured, no prompt is given, or the model fails.
    /// </summary>
    [HttpPost("Interview")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public async Task<ActionResult> Interview([FromBody] InterviewRequest request, CancellationToken cancellationToken)
    {
        var c = Config;
        // "Help me choose" asks the chat model for questions tailored to the typed
        // prompt. Needs the chat endpoint and the model enabled; when disabled,
        // unconfigured, or the prompt is empty it degrades to the generic quiz.
        if (!c.Enabled || !c.UseModel || !Configured(c) || request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Ok(new { questions = Array.Empty<object>() });
        }

        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        var locale = c.ResolveLocale(request.Locale);
        var messages = PromptBuilder.BuildInterviewMessages(request.Prompt!.Trim(), locale);
        // Interview is a small request; an optional cheaper model can serve it (defaults to Model).
        var interviewModel = string.IsNullOrWhiteSpace(c.InterviewModel) ? c.Model : c.InterviewModel;

        string content;
        try
        {
            content = await _directChat.CompleteAsync(c, c.ChatEndpointUrl, c.ChatApiKey, interviewModel, messages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: interview generation failed.");
            return Ok(new { questions = Array.Empty<object>() });
        }

        return Ok(new { questions = ParseInterview(content) });
    }

    /// <summary>Returns the authenticated user's recent searches, newest first.</summary>
    [HttpGet("History")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public ActionResult History()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        // Project to lowercase-keyed anonymous objects: Jellyfin serializes real
        // classes as PascalCase, but the web client reads camelCase (h.mode,
        // it.itemId, ...), matching the Recommend results shape.
        var entries = _history.Load(userId).Select(e => new
        {
            id = e.Id,
            at = e.At,
            prompt = e.Prompt,
            answer = e.Answer,
            mode = e.Mode,
            count = e.Count,
            items = ToResults(e.Items)
        });
        return Ok(new { entries });
    }

    /// <summary>Deletes one history entry, or clears all when no id is given.</summary>
    [HttpDelete("History")]
    [HttpDelete("History/{id}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public ActionResult DeleteHistory(string? id = null)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        _history.Delete(userId, id);
        return Ok(new { });
    }

    /// <summary>Saves a set of movies as a private playlist for the authenticated user.</summary>
    [HttpPost("Playlist")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [AiPopupEndpoint]
    public async Task<ActionResult> Playlist([FromBody] PlaylistRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (_userManager.GetUserById(userId) is null)
        {
            return Unauthorized();
        }

        var ids = ParseGuids(request?.ItemIds).ToList();
        if (ids.Count == 0)
        {
            return BadRequest(new { error = "No valid items." });
        }

        var name = string.IsNullOrWhiteSpace(request?.Name) ? "AI collection" : request!.Name!.Trim();
        try
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = ids,
                UserId = userId,
                MediaType = MediaType.Video
            }).ConfigureAwait(false);
            return Ok(new { id = result.Id, name });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not create playlist.");
            return StatusCode(502, new { error = "Could not create playlist." });
        }
    }

    private void RecordHistory(Guid userId, string conversationId, string prompt, string mode, string answer, List<HistoryItem> items)
    {
        if (userId == Guid.Empty || items.Count == 0)
        {
            return;
        }

        try
        {
            _history.Add(userId, new HistoryEntry
            {
                // The entry id doubles as the conversation id sent to the platform,
                // so "see more" follow-ups (which append to this entry) share it.
                Id = string.IsNullOrEmpty(conversationId) ? Guid.NewGuid().ToString("N") : conversationId,
                At = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Prompt = prompt,
                Answer = answer,
                Mode = mode,
                Count = items.Count,
                Items = items
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not record history.");
        }
    }

    private void AppendHistory(Guid userId, string prompt, string mode, List<HistoryItem> items)
    {
        if (userId == Guid.Empty || items.Count == 0)
        {
            return;
        }

        try
        {
            _history.AppendToLatest(userId, prompt, mode, items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not append to history.");
        }
    }

    // Cheap early-out when logging is off, then never let a logging failure
    // break the response that already succeeded.
    private void EnqueueAiSearchLog(DateTime requestStartedAt, long durationMs, string prompt, string mode, string? answer, List<HistoryItem> items)
    {
        if (!Config.LogSearches)
        {
            return;
        }

        try
        {
            var entry = new SearchLogEntry
            {
                Timestamp = requestStartedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                Kind = "ai",
                Endpoint = HttpContext.Request.Path.Value ?? string.Empty,
                Method = HttpContext.Request.Method,
                StatusCode = StatusCodes.Status200OK,
                DurationMs = durationMs,
                SearchTerm = prompt,
                Mode = mode,
                Answer = answer,
                ResultCount = items.Count,
                TotalRecordCount = null
            };

            SearchLogEntryBuilder.ApplyRequest(entry, HttpContext, _userManager, _libraryManager);
            entry.Results = SearchLogEntryBuilder.FromHistoryItems(items, _libraryManager);
            entry.ResultsLogged = entry.Results.Count;

            _searchLog.Enqueue(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not build a search log entry.");
        }
    }

    // Tolerant parse of {"questions":[{"label","options":[...]}]} into camelCase
    // anonymous objects, capped to keep the UI compact. Options may arrive as
    // plain strings or as {value/label} objects; both are flattened to strings.
    private static object[] ParseInterview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<object>();
        }

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0)
            {
                text = text[(nl + 1)..];
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^3];
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return Array.Empty<object>();
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            if (!doc.RootElement.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<object>();
            }

            var questions = new List<object>();
            foreach (var q in qs.EnumerateArray())
            {
                var label = GetString(q, "label")?.Trim();
                if (string.IsNullOrEmpty(label) || !q.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var options = new List<string>();
                foreach (var opt in opts.EnumerateArray())
                {
                    var value = opt.ValueKind == JsonValueKind.String ? opt.GetString() : GetString(opt, "label") ?? GetString(opt, "value");
                    value = value?.Trim();
                    if (!string.IsNullOrEmpty(value) && !options.Contains(value))
                    {
                        options.Add(value);
                    }

                    if (options.Count >= 5)
                    {
                        break;
                    }
                }

                if (options.Count >= 2)
                {
                    questions.Add(new { label, options });
                }

                if (questions.Count >= 3)
                {
                    break;
                }
            }

            return questions.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<object>();
        }
    }

    // Models often wrap a "plain text" answer in JSON or code fences despite
    // instructions; unwrap {"summary": "..."} and strip fences so the stored
    // profile is clean prose.
    private static string CleanProfileText(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0)
            {
                text = text[(nl + 1)..];
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^3];
            }

            text = text.Trim();
        }

        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var key in new[] { "summary", "profile", "text", "taste" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        return v.GetString()!.Trim();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON after all, fall through and use the raw text.
            }
        }

        return text;
    }

    private static object[] ToResults(List<HistoryItem> items) =>
        items.Select(i => (object)new { itemId = i.ItemId, title = i.Title, year = i.Year, reason = i.Reason }).ToArray();

    private static HashSet<Guid> ParseGuids(string[]? ids)
    {
        var set = new HashSet<Guid>();
        if (ids is null)
        {
            return set;
        }

        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var g))
            {
                set.Add(g);
            }
        }

        return set;
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var g) ? g : Guid.Empty;
    }

    private HttpClient CreateClient(PluginConfiguration c, string key)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(c.TimeoutSeconds, 5, 120));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string TitleYear(BaseItem item) =>
        item.ProductionYear is > 0 ? item.Name + " (" + item.ProductionYear + ")" : item.Name;

    // The user's view of a scope, loaded only by the paths that need it.
    private sealed class LibraryView
    {
        public LibraryView(IReadOnlyList<BaseItem> all) => All = all;

        public IReadOnlyList<BaseItem> All { get; }

        public List<BaseItem> Pool { get; } = new();

        public List<string> Favorites { get; } = new();

        public List<string> Watched { get; } = new();
    }

    // Episodes read better as "Series, S02E05, Title" than a bare episode name.
    private static string? DisplayTitle(BaseItem item) =>
        item is Episode episode ? DocumentBuilder.EpisodeLabel(episode) : item.Name;
}
