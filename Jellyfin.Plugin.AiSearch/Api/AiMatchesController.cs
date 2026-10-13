using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>Request body for <see cref="AiMatchesController.Explain"/>.</summary>
public class ExplainRequest
{
    /// <summary>Gets or sets the search term the matches came from.</summary>
    public string? Term { get; set; }

    /// <summary>Gets or sets the matched item ids, in display order.</summary>
    public string[]? ItemIds { get; set; }

    /// <summary>Gets or sets the UI language ("fr" or "en").</summary>
    public string? Locale { get; set; }
}

/// <summary>
/// Serves the web client's "AI Search" row on Jellyfin's search page. The row
/// is fetched on its own, after Jellyfin's title results are already shown,
/// so a slow embedding or search backend never delays them. Works in native-
/// search-only mode too, since it belongs to Jellyfin's search, not the popup.
/// </summary>
[ApiController]
[Route("AiSearch")]
[Authorize(AuthenticationSchemes = "CustomAuthentication")]
public class AiMatchesController : ControllerBase
{
    private const int MaxExplained = 20;

    // Reasons depend only on the term, the language and the titles shown.
    private static readonly CoalescingCache<Dictionary<string, string>> ReasonCache = new(256, TimeSpan.FromHours(6));

    private readonly SemanticMatcher _matcher;
    private readonly VectorIndex _index;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly DirectChatClient _chat;
    private readonly ILogger<AiMatchesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="AiMatchesController"/> class.</summary>
    public AiMatchesController(
        SemanticMatcher matcher,
        VectorIndex index,
        IUserManager userManager,
        ILibraryManager libraryManager,
        DirectChatClient chat,
        ILogger<AiMatchesController> logger)
    {
        _matcher = matcher;
        _index = index;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _chat = chat;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Meaning-based matches for a search term that Jellyfin's title search
    /// does not already show, each with an instant reason.
    /// </summary>
    /// <param name="term">The search term.</param>
    /// <param name="parentId">Optional library or folder to search within.</param>
    /// <param name="locale">"fr" or "en", for the reasons.</param>
    /// <param name="cancellationToken">Cancelled when the client moves on to another term.</param>
    /// <returns>The row's items.</returns>
    [HttpGet("Matches")]
    public async Task<ActionResult> Matches([FromQuery] string? term, [FromQuery] Guid? parentId, [FromQuery] string? locale, CancellationToken cancellationToken)
    {
        var config = Config;
        term = term?.Trim() ?? string.Empty;
        var skipped = term.Length == 0 ? "empty"
            : Search.SemanticSearchProvider.Unavailable(config, _index)
              ?? (!config.ShowAiSearchRow ? "disabled" : Search.SemanticSearchProvider.TooShort(config, term) ? "too_short" : null);
        if (skipped is not null)
        {
            return Ok(new { term, skipped, items = Array.Empty<object>() });
        }

        var user = GetUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var french = string.Equals(locale, "fr", StringComparison.OrdinalIgnoreCase);
        var take = Math.Clamp(config.SemanticSearchMaxResults, 1, 100);
        var stopwatch = Stopwatch.StartNew();

        // Nothing waits on this request but the row itself, so it may take the
        // backend's full timeout; the client cancels it when the term changes.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 30)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        SemanticMatchResult found;
        try
        {
            var kinds = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };
            found = await _matcher.FindAsync(config, term, user, kinds, parentId, take, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Ok(new { term, skipped = "timeout", items = Array.Empty<object>() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("AiSearch: AI Search row retrieval failed: {Message}", ex.Message);
            return Ok(new { term, skipped = "error", items = Array.Empty<object>() });
        }

        // Title matches are already in Jellyfin's own rows above.
        var items = found.Matches
            .Where(m => !m.TitleMatch)
            .Select(m =>
            {
                var item = m.Item;
                var people = _libraryManager.GetPeople(item).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(12).ToList();
                var explanation = MatchExplainer.Explain(item, term, people, french);
                var episode = item as Episode;
                return new
                {
                    id = item.Id.ToString("N"),
                    name = item.Name,
                    type = item is Episode ? "Episode" : item is Series ? "Series" : "Movie",
                    year = item.ProductionYear,
                    seriesName = episode?.SeriesName,
                    seasonNumber = episode?.ParentIndexNumber,
                    episodeNumber = episode?.IndexNumber,
                    similarity = m.Similarity,
                    why = explanation.Why,
                    snippet = explanation.Snippet
                };
            })
            .ToList();

        return Ok(new
        {
            term,
            skipped = (string?)null,
            mode = found.Mode,
            cached = found.Cached,
            durationMs = stopwatch.ElapsedMilliseconds,
            items
        });
    }

    /// <summary>
    /// One model-written reason per matched title, for the AI Search row once
    /// the search settles. Empty when the option is off or no chat model is
    /// configured; the row then keeps its instant reasons.
    /// </summary>
    /// <param name="request">The term, ids and language.</param>
    /// <param name="cancellationToken">Cancelled when the client moves on.</param>
    /// <returns>A map of item id to reason.</returns>
    [HttpPost("Explain")]
    public async Task<ActionResult> Explain([FromBody] ExplainRequest request, CancellationToken cancellationToken)
    {
        var config = Config;
        var empty = new { reasons = new Dictionary<string, string>() };
        if (!config.Enabled || !config.ExplainAiMatches || !config.UseModel || string.IsNullOrWhiteSpace(config.ChatEndpointUrl)
            || request is null || string.IsNullOrWhiteSpace(request.Term) || request.ItemIds is not { Length: > 0 })
        {
            return Ok(empty);
        }

        var user = GetUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var ids = request.ItemIds
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .Take(MaxExplained)
            .ToList();

        // Only titles this user may see are described.
        var items = ids
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null && item.IsVisibleStandalone(user))
            .Cast<BaseItem>()
            .ToList();
        if (items.Count == 0)
        {
            return Ok(empty);
        }

        var term = request.Term.Trim();
        var locale = string.Equals(request.Locale, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        var model = string.IsNullOrWhiteSpace(config.InterviewModel) ? config.Model : config.InterviewModel;
        var key = string.Join('\n', model, locale, term, string.Join(',', items.Select(i => i.Id.ToString("N"))));
        try
        {
            var (reasons, _) = await ReasonCache.GetAsync(
                key,
                async token =>
                {
                    var messages = PromptBuilder.BuildExplainMessages(term, locale, items);
                    var content = await _chat.CompleteAsync(config, config.ChatEndpointUrl, config.ChatApiKey, model, messages, token).ConfigureAwait(false);
                    var parsed = ModelResponseParser.Parse(content) ?? throw new InvalidOperationException("unreadable model reply");
                    var map = new Dictionary<string, string>();
                    foreach (var rec in parsed.Recommendations)
                    {
                        if (rec.Index >= 0 && rec.Index < items.Count && !string.IsNullOrWhiteSpace(rec.Reason))
                        {
                            map[items[rec.Index].Id.ToString("N")] = rec.Reason.Trim();
                        }
                    }

                    return map;
                },
                TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120)),
                cancellationToken).ConfigureAwait(false);
            return Ok(new { reasons });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "AiSearch: explaining AI Search matches failed.");
            return Ok(empty);
        }
    }

    private Jellyfin.Database.Implementations.Entities.User? GetUser()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? _userManager.GetUserById(id) : null;
    }
}
