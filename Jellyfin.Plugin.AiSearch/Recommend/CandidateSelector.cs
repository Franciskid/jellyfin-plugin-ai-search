using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// The non-semantic candidate picker (direct mode's fallback): choose which
/// slice of the catalog the model gets to see when there is no index —
/// top-rated, a random sample, or half of each.
/// </summary>
public static class CandidateSelector
{
    /// <summary>Selects up to <paramref name="count"/> candidates from the pool.</summary>
    /// <param name="pool">User-visible, already watched-filtered movies.</param>
    /// <param name="strategy">"top", "random", or "mix".</param>
    /// <param name="count">Maximum candidates to return.</param>
    /// <returns>The selected movies.</returns>
    public static List<BaseItem> Select(List<BaseItem> pool, string strategy, int count)
    {
        if (pool.Count <= count)
        {
            return pool.OrderByDescending(item => item.CommunityRating ?? 0f).ToList();
        }

        switch ((strategy ?? "top").ToLowerInvariant())
        {
            case "random":
                return Shuffle(pool).Take(count).ToList();
            case "mix":
                var half = count / 2;
                var top = pool.OrderByDescending(item => item.CommunityRating ?? 0f).Take(half).ToList();
                var taken = new HashSet<Guid>(top.Select(item => item.Id));
                var rest = Shuffle(pool.Where(item => !taken.Contains(item.Id)).ToList()).Take(count - half);
                return top.Concat(rest).ToList();
            default:
                return pool.OrderByDescending(item => item.CommunityRating ?? 0f).Take(count).ToList();
        }
    }

    private static List<BaseItem> Shuffle(List<BaseItem> items)
    {
        var rng = Random.Shared;
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }
}
