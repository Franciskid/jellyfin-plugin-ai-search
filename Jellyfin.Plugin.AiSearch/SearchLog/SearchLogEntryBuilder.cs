using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.AiSearch.SearchLog;

/// <summary>
/// Fills in the parts of a <see cref="SearchLogEntry"/> that both capture paths
/// (the native-search filter and the AI popup controller) need the same way:
/// who called, its query-string parameters with credentials redacted, and the
/// per-result rows, including which ones were semantic matches.
/// </summary>
internal static class SearchLogEntryBuilder
{
    private const int MaxResults = 250;
    private static readonly string[] RedactedKeyFragments = { "apikey", "api_key", "token", "auth", "password", "pw" };

    /// <summary>Fills the caller-identity and query-parameter fields from the current request.</summary>
    /// <param name="entry">The entry being built.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="userManager">User manager, to resolve the user name.</param>
    /// <param name="libraryManager">Library manager, to resolve the parent item's name.</param>
    public static void ApplyRequest(SearchLogEntry entry, HttpContext context, IUserManager userManager, ILibraryManager libraryManager)
    {
        var claims = context.User;

        // The `userId` query parameter, when present and valid, is whose view
        // Jellyfin actually searched (it wins over the caller's own claim, e.g.
        // an admin searching as another user); the claim is the fallback. Never
        // resolve Guid.Empty: IUserManager.GetUserById throws on it, and
        // "Jellyfin-UserId" is a non-empty all-zero string for API-key calls.
        Guid? effectiveUserId = null;
        if (context.Request.Query.TryGetValue("userId", out var userIdParam)
            && Guid.TryParse(userIdParam.ToString(), out var paramGuid)
            && paramGuid != Guid.Empty)
        {
            effectiveUserId = paramGuid;
        }
        else
        {
            var claimUserId = claims.FindFirst("Jellyfin-UserId")?.Value;
            if (!string.IsNullOrEmpty(claimUserId) && Guid.TryParse(claimUserId, out var claimGuid) && claimGuid != Guid.Empty)
            {
                effectiveUserId = claimGuid;
            }
        }

        entry.UserId = effectiveUserId?.ToString("N");
        entry.UserName = effectiveUserId is Guid resolvedUserId ? userManager.GetUserById(resolvedUserId)?.Username : null;

        entry.IsApiKey = string.Equals(claims.FindFirst("Jellyfin-IsApiKey")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        entry.Client = NullIfEmpty(claims.FindFirst("Jellyfin-Client")?.Value);
        entry.ClientVersion = NullIfEmpty(claims.FindFirst("Jellyfin-Version")?.Value);
        entry.Device = NullIfEmpty(claims.FindFirst("Jellyfin-Device")?.Value);
        entry.DeviceId = NullIfEmpty(claims.FindFirst("Jellyfin-DeviceId")?.Value);
        entry.RemoteAddress = context.Connection.RemoteIpAddress?.ToString();
        entry.UserAgent = NullIfEmpty(context.Request.Headers.UserAgent.ToString());
        entry.Parameters = BuildParameters(context.Request.Query);

        if (context.Request.Query.TryGetValue("parentId", out var parentIdParam) && Guid.TryParse(parentIdParam.ToString(), out var parentId))
        {
            entry.ParentId = parentId.ToString("N");
            entry.ParentName = libraryManager.GetItemById(parentId)?.Name;
        }
    }

    /// <summary>Maps native-search <see cref="BaseItemDto"/> results, capped at 250, marking semantic hits from the trace.</summary>
    public static List<SearchLogResultEntry> FromItems(IReadOnlyList<BaseItemDto> items, SemanticSearchTrace? trace)
    {
        var list = new List<SearchLogResultEntry>(Math.Min(items.Count, MaxResults));
        for (var i = 0; i < items.Count && list.Count < MaxResults; i++)
        {
            var dto = items[i];
            (float Similarity, int Rank) hit = default;
            var hasHit = trace is not null && trace.Hits.TryGetValue(dto.Id, out hit);
            list.Add(new SearchLogResultEntry
            {
                Position = i + 1,
                ItemId = dto.Id.ToString("N"),
                Name = dto.Name,
                Type = dto.Type.ToString(),
                Year = dto.ProductionYear,
                SeriesName = dto.SeriesName,
                SeasonNumber = dto.ParentIndexNumber,
                EpisodeNumber = dto.IndexNumber,
                Semantic = hasHit,
                SemanticScore = hasHit ? hit.Similarity : null,
                SemanticRank = hasHit ? hit.Rank : null
            });
        }

        return list;
    }

    /// <summary>Maps native-search <see cref="SearchHint"/> results, capped at 250, marking semantic hits from the trace.</summary>
    public static List<SearchLogResultEntry> FromHints(IReadOnlyList<SearchHint> hints, SemanticSearchTrace? trace)
    {
        var list = new List<SearchLogResultEntry>(Math.Min(hints.Count, MaxResults));
        for (var i = 0; i < hints.Count && list.Count < MaxResults; i++)
        {
            var hint = hints[i];
            (float Similarity, int Rank) hit = default;
            var hasHit = trace is not null && trace.Hits.TryGetValue(hint.Id, out hit);
            list.Add(new SearchLogResultEntry
            {
                Position = i + 1,
                ItemId = hint.Id.ToString("N"),
                Name = hint.Name,
                Type = hint.Type.ToString(),
                Year = hint.ProductionYear,
                SeriesName = hint.Series,
                SeasonNumber = hint.ParentIndexNumber,
                EpisodeNumber = hint.IndexNumber,
                Semantic = hasHit,
                SemanticScore = hasHit ? hit.Similarity : null,
                SemanticRank = hasHit ? hit.Rank : null
            });
        }

        return list;
    }

    /// <summary>Maps AI popup <see cref="HistoryItem"/> results, capped at 250, resolving type/series/episode from the library.</summary>
    public static List<SearchLogResultEntry> FromHistoryItems(IReadOnlyList<HistoryItem> items, ILibraryManager libraryManager)
    {
        var list = new List<SearchLogResultEntry>(Math.Min(items.Count, MaxResults));
        for (var i = 0; i < items.Count && list.Count < MaxResults; i++)
        {
            var historyItem = items[i];
            var row = new SearchLogResultEntry
            {
                Position = i + 1,
                ItemId = historyItem.ItemId,
                Name = historyItem.Title,
                Year = historyItem.Year,
                Reason = historyItem.Reason
            };

            if (Guid.TryParse(historyItem.ItemId, out var itemId) && libraryManager.GetItemById(itemId) is BaseItem item)
            {
                row.Type = item switch
                {
                    Episode => nameof(Episode),
                    Series => nameof(Series),
                    _ => item.GetType().Name
                };
                if (item is Episode episode)
                {
                    row.SeriesName = episode.SeriesName;
                    row.SeasonNumber = episode.ParentIndexNumber;
                    row.EpisodeNumber = episode.IndexNumber;
                }
            }

            list.Add(row);
        }

        return list;
    }

    private static Dictionary<string, string> BuildParameters(IQueryCollection query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query)
        {
            if (RedactedKeyFragments.Any(fragment => pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result[pair.Key] = string.Join(",", pair.Value.Where(v => v is not null));
        }

        return result;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
