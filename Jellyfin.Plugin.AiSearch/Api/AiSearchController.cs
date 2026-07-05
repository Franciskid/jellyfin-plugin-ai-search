using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>Gets or sets item ids to exclude — used by "show more" to avoid repeats.</summary>
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
/// Authenticates the caller and returns movie recommendations. In
/// <c>platform</c> mode it sends the prompt + user to the configured platform
/// (which runs a semantic search over the library); in <c>direct</c> mode it
/// retrieves candidates itself — from the plugin's own semantic index when one
/// is built, or by catalog selection otherwise — and calls any
/// OpenAI-compatible endpoint.
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
    private readonly DirectChatClient _directChat;
    private readonly IPlaylistManager _playlistManager;
    private readonly HistoryStore _history;
    private readonly TasteProfileStore _taste;
    private readonly ILogger<AiSearchController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AiSearchController"/> class.</summary>
    public AiSearchController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IHttpClientFactory httpClientFactory,
        SemanticRetriever semanticRetriever,
        DirectChatClient directChat,
        IPlaylistManager playlistManager,
        HistoryStore history,
        TasteProfileStore taste,
        ILogger<AiSearchController> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _httpClientFactory = httpClientFactory;
        _semanticRetriever = semanticRetriever;
        _directChat = directChat;
        _playlistManager = playlistManager;
        _history = history;
        _taste = taste;
        _logger = logger;
    }

    // Synthetic request for surprise mode — the model still personalizes via the
    // user's favorites/watched signals, but is steered toward variety.
    private const string SurprisePrompt =
        "Surprise me. From the candidates, pick a delightfully varied, unexpected mix I might enjoy " +
        "but would not think to search for. Favor variety across genres and eras over obvious blockbusters.";

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static bool IsDirect => string.Equals(Config.Mode, "direct", StringComparison.OrdinalIgnoreCase);

    private static string ClientVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    private static bool Configured(PluginConfiguration c) => IsDirect
        ? !string.IsNullOrWhiteSpace(c.DirectApiKey) && !string.IsNullOrWhiteSpace(c.DirectEndpointUrl)
        : !string.IsNullOrWhiteSpace(c.ApiKey) && !string.IsNullOrWhiteSpace(c.PlatformApiUrl);

    /// <summary>Serves the injected web-client script (no auth; contains no secrets).</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    public ActionResult ClientScript()
    {
        var stream = GetType().Assembly
            .GetManifestResourceStream("Jellyfin.Plugin.AiSearch.Web.ai-search.js");
        return stream is null ? NotFound() : File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>Lightweight status endpoint so the client knows whether AI search is usable.</summary>
    [HttpGet("Health")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult Health()
    {
        var c = Config;
        return Ok(new
        {
            enabled = c.Enabled,
            configured = Configured(c),
            mode = IsDirect ? "direct" : "platform",
            model = c.Model,
            maxResults = c.MaxResults
        });
    }

    /// <summary>Proxies the model-alias list for the admin config dropdown.</summary>
    [HttpGet("Models")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> Models(CancellationToken cancellationToken)
    {
        var c = Config;
        if (!Configured(c))
        {
            return Ok(new { data = Array.Empty<object>() });
        }

        var (baseUrl, key, path) = IsDirect
            ? (c.DirectEndpointUrl, c.DirectApiKey, "/v1/models")
            : (c.PlatformApiUrl, c.ApiKey, "/api/media/models");
        try
        {
            using var client = CreateClient(c, key);
            using var resp = await client.GetAsync(Combine(baseUrl, path), cancellationToken).ConfigureAwait(false);
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
    public async Task<ActionResult> Recommend([FromBody] RecommendRequest request, CancellationToken cancellationToken)
    {
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
        var locale = string.Equals(request.Locale, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
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

        if (!IsDirect)
        {
            return await RecommendPlatform(c, modelPrompt, locale, maxResults, user.Username, userId, includeWatched, historyPrompt, mode, record, cancellationToken).ConfigureAwait(false);
        }

        // Direct mode: one pass over the user's library view gathers the
        // watch-filtered pool plus taste signals (captures the untyped `user`
        // so we never name the Jellyfin user-entity type across versions).
        // `movies` holds the scope's items — movies, or TV series + episodes.
        var scopeKinds = isTv
            ? new[] { BaseItemKind.Series, BaseItemKind.Episode }
            : new[] { BaseItemKind.Movie };
        var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = scopeKinds,
            Recursive = true,
            IsVirtualItem = false
        });

        var pool = new List<BaseItem>();
        var watched = new List<string>();
        var favorites = new List<string>();
        var playedById = new Dictionary<Guid, bool>(movies.Count);
        foreach (var item in movies)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var ud = _userDataManager.GetUserData(user, item);
            var played = ud?.Played ?? false;
            playedById.TryAdd(item.Id, played);
            if (personalize && (ud?.IsFavorite ?? false) && favorites.Count < 60)
            {
                favorites.Add(TitleYear(item));
            }

            if (personalize && played && watched.Count < 80)
            {
                watched.Add(TitleYear(item));
            }

            if ((includeWatched || !played) && !exclude.Contains(item.Id))
            {
                pool.Add(item);
            }
        }

        if (pool.Count == 0)
        {
            pool.AddRange(movies.Where(m => !string.IsNullOrWhiteSpace(m.Name) && !exclude.Contains(m.Id)));
        }

        // Surprise mode skips the semantic index (there is no query to match)
        // and hands the model a broad random slice to pick delightful picks from.
        // For TV scope, restrict to series — surprising the user with a single
        // episode rather than a show to watch isn't the point of the feature.
        List<BaseItem>? candidates;
        bool usedSemantic;
        if (isSurprise)
        {
            var surprisePool = isTv ? pool.Where(item => item is Series).ToList() : pool;
            if (isTv && surprisePool.Count == 0)
            {
                surprisePool = movies.Where(item => item is Series && !exclude.Contains(item.Id)).ToList();
            }

            candidates = CandidateSelector.Select(surprisePool, "random", Math.Max(10, c.MaxCatalogItems));
            usedSemantic = false;
        }
        else
        {
            // Prefer the semantic index; fall back to catalog selection when it is
            // not built/configured or the embedding call fails.
            candidates = await SemanticCandidatesAsync(c, modelPrompt, movies, playedById, includeWatched, exclude, isTv, cancellationToken).ConfigureAwait(false);
            usedSemantic = candidates is not null;
            candidates ??= CandidateSelector.Select(pool, c.SelectionStrategy, Math.Max(10, c.MaxCatalogItems));
        }

        // Resolve cast from live metadata only on the semantic path (≤ ~40
        // candidates); the fallback dump would mean hundreds of people lookups.
        var enriched = candidates
            .Select(item => new CandidateMovie(
                item,
                usedSemantic ? CastFormatter.Format(_libraryManager.GetPeople(item)) : string.Empty))
            .ToList();

        // Distilled taste summary (silent): fed into the prompt when personalizing,
        // and lazily (re)built in the background as the user's taste drifts.
        var profileText = personalize ? _taste.Load(userId)?.Text : null;
        if (personalize)
        {
            MaybeRefreshProfile(c, userId, favorites, watched, locale);
        }

        return await RecommendDirect(c, modelPrompt, locale, maxResults, enriched, favorites, watched, usedSemantic, userId, historyPrompt, mode, record, profileText, cancellationToken).ConfigureAwait(false);
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
                var text = CleanProfileText(await _directChat.CompleteAsync(c, messages, CancellationToken.None).ConfigureAwait(false) ?? string.Empty);
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

    // --- Direct mode: semantic retrieval over the plugin's own index. ---
    private async Task<List<BaseItem>?> SemanticCandidatesAsync(
        PluginConfiguration c,
        string prompt,
        IReadOnlyList<BaseItem> movies,
        Dictionary<Guid, bool> playedById,
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
            var series = DetectSeries(prompt, movies);
            if (series is not null)
            {
                allowedIds = new HashSet<Guid>();
                foreach (var item in movies)
                {
                    if ((item is Episode ep && ep.SeriesId == series.Id) || item.Id == series.Id)
                    {
                        allowedIds.Add(item.Id);
                    }
                }

                if (allowedIds.Count == 0)
                {
                    allowedIds = null; // nothing embedded for it; fall back to global
                }
                else
                {
                    take = System.Math.Max(maxRetrieve, allowedIds.Count);
                }
            }
        }

        var ids = await _semanticRetriever.TryRetrieveAsync(c, prompt, take, allowedIds, cancellationToken).ConfigureAwait(false);
        if (ids is null)
        {
            return null;
        }

        // The index is library-wide; intersecting with the user's own item
        // list enforces per-user visibility.
        var byId = new Dictionary<Guid, BaseItem>(movies.Count);
        foreach (var item in movies)
        {
            byId.TryAdd(item.Id, item);
        }

        var picked = new List<BaseItem>(maxRetrieve);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item))
            {
                continue;
            }

            if (exclude.Contains(id))
            {
                continue;
            }

            if (!includeWatched && playedById.TryGetValue(id, out var played) && played)
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

    // Finds the library series whose name appears verbatim (case-insensitive) in
    // the query, preferring the longest such name so "Star Trek: TNG" beats
    // "Star Trek". Returns null when the query names no held series.
    private static BaseItem? DetectSeries(string prompt, IReadOnlyList<BaseItem> movies)
    {
        var q = prompt.ToLowerInvariant();
        BaseItem? best = null;
        var bestLen = 0;
        foreach (var item in movies)
        {
            if (item is not Series || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length < 3)
            {
                continue;
            }

            if (item.Name.Length > bestLen && q.Contains(item.Name.ToLowerInvariant(), System.StringComparison.Ordinal))
            {
                best = item;
                bestLen = item.Name.Length;
            }
        }

        return best;
    }

    // --- Platform mode: the platform does the semantic search. ---
    private async Task<ActionResult> RecommendPlatform(
        PluginConfiguration c, string prompt, string locale, int maxResults, string userName, Guid userId, bool includeWatched, string historyPrompt, string mode, bool record, CancellationToken cancellationToken)
    {
        var payload = new
        {
            prompt,
            model = string.IsNullOrWhiteSpace(c.Model) ? null : c.Model,
            maxResults,
            maxRetrieve = c.MaxRetrieve,
            includeWatched,
            locale,
            user = new { id = userId.ToString("N"), name = userName },
            client = new { name = "jellyfin-ai-search", version = ClientVersion }
        };

        string raw;
        try
        {
            using var client = CreateClient(c, c.ApiKey);
            using var resp = await client
                .PostAsJsonAsync(Combine(c.PlatformApiUrl, "/api/media/recommend"), payload, cancellationToken)
                .ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AiSearch: platform returned {Status}: {Body}", (int)resp.StatusCode, Trunc(raw, 300));
                return StatusCode((int)resp.StatusCode == 503 ? 503 : 502, new { error = "AI service error." });
            }
        }
        catch (TaskCanceledException)
        {
            return StatusCode(504, new { error = "AI service timed out." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: platform call failed.");
            return StatusCode(502, new { error = "AI service unavailable." });
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var answer = GetString(root, "answer");
            var model = GetString(root, "model") ?? c.Model;
            var usedProfile = root.TryGetProperty("usedProfile", out var up) && up.ValueKind == JsonValueKind.True;
            var items = new List<HistoryItem>();
            if (root.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in recs.EnumerateArray())
                {
                    var itemId = GetString(rec, "itemId");
                    if (string.IsNullOrEmpty(itemId))
                    {
                        continue;
                    }

                    items.Add(new HistoryItem
                    {
                        ItemId = itemId,
                        Title = GetString(rec, "title"),
                        Year = rec.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (int?)null,
                        Reason = GetString(rec, "reason")
                    });
                    if (items.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            if (record)
            {
                RecordHistory(userId, historyPrompt, mode, items);
            }
            else
            {
                AppendHistory(userId, historyPrompt, mode, items);
            }

            return Ok(new { answer, model, usedProfile, results = ToResults(items) });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: unexpected platform response: {Body}", Trunc(raw, 300));
            return StatusCode(502, new { error = "Unexpected AI response." });
        }
    }

    // --- Direct mode: candidates in, OpenAI-compatible completion out. ---
    private async Task<ActionResult> RecommendDirect(
        PluginConfiguration c, string prompt, string locale, int maxResults, List<CandidateMovie> candidates, List<string> favorites, List<string> watched, bool usedSemantic, Guid userId, string historyPrompt, string mode, bool record, string? tasteProfile, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Ok(new { answer = "Your movie library looks empty.", model = c.Model, usedProfile = false, results = Array.Empty<object>() });
        }

        // Cast + synopsis only with semantic retrieval: ~40 rich lines help the
        // model judge fit, while hundreds of them would explode the token count.
        var messages = PromptBuilder.BuildMessages(prompt, locale, maxResults, candidates, favorites, watched, includeDetails: usedSemantic, tasteProfile: tasteProfile);

        string content;
        try
        {
            content = await _directChat.CompleteAsync(c, messages, cancellationToken).ConfigureAwait(false);
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
            RecordHistory(userId, historyPrompt, mode, items);
        }
        else
        {
            AppendHistory(userId, historyPrompt, mode, items);
        }

        var usedProfile = favorites.Count > 0 || watched.Count > 0 || !string.IsNullOrWhiteSpace(tasteProfile);
        return Ok(new { answer = parsed.Answer, model = c.Model, usedProfile, results = ToResults(items) });
    }

    /// <summary>
    /// Returns a few model-generated multiple-choice questions tailored to the
    /// caller's initial prompt, for the "Help me choose" flow. Degrades to an
    /// empty list (client falls back to its generic questions) when direct mode
    /// is off, nothing is configured, no prompt is given, or the model fails.
    /// </summary>
    [HttpPost("Interview")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult> Interview([FromBody] InterviewRequest request, CancellationToken cancellationToken)
    {
        var c = Config;
        if (!c.Enabled || !IsDirect || !Configured(c) || request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Ok(new { questions = Array.Empty<object>() });
        }

        if (GetUserId() == Guid.Empty)
        {
            return Unauthorized();
        }

        var locale = string.Equals(request.Locale, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        var messages = PromptBuilder.BuildInterviewMessages(request.Prompt!.Trim(), locale);

        string content;
        try
        {
            content = await _directChat.CompleteAsync(c, messages, cancellationToken).ConfigureAwait(false);
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
    public ActionResult History()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        // Project to lowercase-keyed anonymous objects: Jellyfin serializes real
        // classes as PascalCase, but the web client reads camelCase (h.mode,
        // it.itemId, …) — matching the Recommend results shape.
        var entries = _history.Load(userId).Select(e => new
        {
            id = e.Id,
            at = e.At,
            prompt = e.Prompt,
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

    private void RecordHistory(Guid userId, string prompt, string mode, List<HistoryItem> items)
    {
        if (userId == Guid.Empty || items.Count == 0)
        {
            return;
        }

        try
        {
            _history.Add(userId, new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                At = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Prompt = prompt,
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
                // Not JSON after all — fall through and use the raw text.
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

    private static string Combine(string baseUrl, string path) => baseUrl.TrimEnd('/') + path;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string TitleYear(BaseItem item) =>
        item.ProductionYear is > 0 ? item.Name + " (" + item.ProductionYear + ")" : item.Name;

    // Episodes read better as "Series — S02E05 — Title" than a bare episode name.
    private static string? DisplayTitle(BaseItem item) =>
        item is Episode episode ? DocumentBuilder.EpisodeLabel(episode) : item.Name;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}
