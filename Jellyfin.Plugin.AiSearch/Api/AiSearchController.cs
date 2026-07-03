using System;
using System.Collections.Generic;
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
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>Public request body for a recommendation.</summary>
public class RecommendRequest
{
    /// <summary>Gets or sets the natural-language prompt.</summary>
    public string? Prompt { get; set; }

    /// <summary>Gets or sets an optional per-request result cap.</summary>
    public int? MaxResults { get; set; }

    /// <summary>Gets or sets the UI language ("fr" or "en") to answer in.</summary>
    public string? Locale { get; set; }
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
    private readonly ILogger<AiSearchController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AiSearchController"/> class.</summary>
    public AiSearchController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IHttpClientFactory httpClientFactory,
        SemanticRetriever semanticRetriever,
        DirectChatClient directChat,
        ILogger<AiSearchController> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _httpClientFactory = httpClientFactory;
        _semanticRetriever = semanticRetriever;
        _directChat = directChat;
        _logger = logger;
    }

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
            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: failed to list models.");
            return Ok(new { data = Array.Empty<object>(), error = ex.Message });
        }
    }

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

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
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
        var prompt = request.Prompt!.Trim();

        if (!IsDirect)
        {
            return await RecommendPlatform(c, prompt, locale, maxResults, user.Username, userId, cancellationToken).ConfigureAwait(false);
        }

        // Direct mode: one pass over the user's library view gathers the
        // watch-filtered pool plus taste signals (captures the untyped `user`
        // so we never name the Jellyfin user-entity type across versions).
        var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
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
            if ((ud?.IsFavorite ?? false) && favorites.Count < 60)
            {
                favorites.Add(TitleYear(item));
            }

            if (played && watched.Count < 80)
            {
                watched.Add(TitleYear(item));
            }

            if (c.IncludeWatched || !played)
            {
                pool.Add(item);
            }
        }

        if (pool.Count == 0)
        {
            pool.AddRange(movies.Where(m => !string.IsNullOrWhiteSpace(m.Name)));
        }

        // Prefer the semantic index; fall back to catalog selection when it is
        // not built/configured or the embedding call fails.
        var candidates = await SemanticCandidatesAsync(c, prompt, movies, playedById, cancellationToken).ConfigureAwait(false);
        var usedSemantic = candidates is not null;
        candidates ??= CandidateSelector.Select(pool, c.SelectionStrategy, Math.Max(10, c.MaxCatalogItems));

        // Resolve cast from live metadata only on the semantic path (≤ ~40
        // candidates); the fallback dump would mean hundreds of people lookups.
        var enriched = candidates
            .Select(item => new CandidateMovie(
                item,
                usedSemantic ? CastFormatter.Format(_libraryManager.GetPeople(item)) : string.Empty))
            .ToList();
        return await RecommendDirect(c, prompt, locale, maxResults, enriched, favorites, watched, usedSemantic, cancellationToken).ConfigureAwait(false);
    }

    // --- Direct mode: semantic retrieval over the plugin's own index. ---
    private async Task<List<BaseItem>?> SemanticCandidatesAsync(
        PluginConfiguration c,
        string prompt,
        IReadOnlyList<BaseItem> movies,
        Dictionary<Guid, bool> playedById,
        CancellationToken cancellationToken)
    {
        var maxRetrieve = Math.Clamp(c.MaxRetrieve, 1, 200);

        // Over-fetch so watched/visibility filtering still leaves enough.
        var ids = await _semanticRetriever.TryRetrieveAsync(c, prompt, maxRetrieve * 3, cancellationToken).ConfigureAwait(false);
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

            if (!c.IncludeWatched && playedById.TryGetValue(id, out var played) && played)
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

    // --- Platform mode: the platform does the semantic search. ---
    private async Task<ActionResult> RecommendPlatform(
        PluginConfiguration c, string prompt, string locale, int maxResults, string userName, Guid userId, CancellationToken cancellationToken)
    {
        var payload = new
        {
            prompt,
            model = string.IsNullOrWhiteSpace(c.Model) ? null : c.Model,
            maxResults,
            maxRetrieve = c.MaxRetrieve,
            includeWatched = c.IncludeWatched,
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
            var results = new List<object>();
            if (root.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in recs.EnumerateArray())
                {
                    var itemId = GetString(rec, "itemId");
                    if (string.IsNullOrEmpty(itemId))
                    {
                        continue;
                    }

                    results.Add(new
                    {
                        itemId,
                        title = GetString(rec, "title"),
                        year = rec.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (int?)null,
                        reason = GetString(rec, "reason")
                    });
                    if (results.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            return Ok(new { answer, model, usedProfile, results });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: unexpected platform response: {Body}", Trunc(raw, 300));
            return StatusCode(502, new { error = "Unexpected AI response." });
        }
    }

    // --- Direct mode: candidates in, OpenAI-compatible completion out. ---
    private async Task<ActionResult> RecommendDirect(
        PluginConfiguration c, string prompt, string locale, int maxResults, List<CandidateMovie> candidates, List<string> favorites, List<string> watched, bool usedSemantic, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Ok(new { answer = "Your movie library looks empty.", model = c.Model, usedProfile = false, results = Array.Empty<object>() });
        }

        // Cast + synopsis only with semantic retrieval: ~40 rich lines help the
        // model judge fit, while hundreds of them would explode the token count.
        var messages = PromptBuilder.BuildMessages(prompt, locale, maxResults, candidates, favorites, watched, includeDetails: usedSemantic);

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

        var results = new List<object>();
        foreach (var rec in parsed.Recommendations)
        {
            if (rec.Index < 0 || rec.Index >= candidates.Count)
            {
                continue;
            }

            var item = candidates[rec.Index].Item;
            results.Add(new
            {
                itemId = item.Id.ToString("N"),
                title = item.Name,
                year = item.ProductionYear,
                reason = rec.Reason
            });
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return Ok(new { answer = parsed.Answer, model = c.Model, usedProfile = favorites.Count > 0 || watched.Count > 0, results });
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

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}
