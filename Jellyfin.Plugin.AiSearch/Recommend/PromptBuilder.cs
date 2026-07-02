using System.Collections.Generic;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// Builds the system + user messages for a direct-mode recommendation. The
/// model only ever picks by index from the CANDIDATES list, which keeps it
/// from inventing titles that are not in the library.
/// </summary>
public static class PromptBuilder
{
    private const int SynopsisLength = 180;

    /// <summary>Builds the chat messages for an OpenAI-compatible completion call.</summary>
    /// <param name="prompt">The user's request.</param>
    /// <param name="locale">"fr" or "en" — the language for answer and reasons.</param>
    /// <param name="maxResults">How many recommendations to ask for.</param>
    /// <param name="candidates">The movies the model may choose from.</param>
    /// <param name="favorites">Title (year) list of the user's favorites, as a taste signal.</param>
    /// <param name="watched">Title (year) list of watched movies, as a taste signal.</param>
    /// <param name="includeSynopses">
    /// Whether to append a short overview per candidate. On for semantic
    /// retrieval (≤ ~60 rich candidates); off for the legacy catalog dump,
    /// where hundreds of synopses would explode the token count.
    /// </param>
    /// <returns>The messages array for the completions payload.</returns>
    public static object[] BuildMessages(
        string prompt,
        string locale,
        int maxResults,
        List<BaseItem> candidates,
        List<string> favorites,
        List<string> watched,
        bool includeSynopses)
    {
        var language = locale == "fr"
            ? "Write the \"answer\" and every \"reason\" in French. "
            : "Write the \"answer\" and every \"reason\" in English. ";
        var system =
            "You are a film recommendation engine for a personal Jellyfin movie library. " +
            "Recommend ONLY movies from the provided CANDIDATES list, chosen to match the user's request and taste. " +
            "Never invent titles or indices that are not in the list. Prefer variety and quality. " + language +
            "Respond with STRICT minified JSON only (no markdown, no prose) of exactly this shape: " +
            "{\"answer\":\"one short sentence\",\"recommendations\":[{\"index\":<integer from the list>,\"reason\":\"<=140 chars why it fits\"}]}. " +
            "Return up to " + maxResults + " recommendations: when several candidates genuinely match, return close to " +
            "that many rather than only the most obvious ones; return fewer only when the list truly lacks matches.";

        var user = new StringBuilder();
        user.Append("REQUEST: ").Append(prompt).Append('\n');
        if (favorites.Count > 0)
        {
            user.Append("USER_FAVORITES: ").Append(string.Join("; ", favorites)).Append('\n');
        }

        if (watched.Count > 0)
        {
            user.Append("USER_WATCHED: ").Append(string.Join("; ", watched)).Append('\n');
        }

        user.Append(includeSynopses
            ? "CANDIDATES (index|title (year)|genres|synopsis):\n"
            : "CANDIDATES (index|title (year)|genres):\n");
        for (var i = 0; i < candidates.Count; i++)
        {
            AppendCandidateLine(user, i, candidates[i], includeSynopses);
        }

        return new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user.ToString() },
        };
    }

    private static void AppendCandidateLine(StringBuilder user, int index, BaseItem item, bool includeSynopsis)
    {
        user.Append(index).Append('|').Append(Clean(item.Name));
        if (item.ProductionYear is > 0)
        {
            user.Append(" (").Append(item.ProductionYear).Append(')');
        }

        if (item.Genres is { Length: > 0 })
        {
            user.Append('|').Append(string.Join(',', item.Genres[..System.Math.Min(4, item.Genres.Length)]));
        }

        if (includeSynopsis && !string.IsNullOrWhiteSpace(item.Overview))
        {
            user.Append('|').Append(Snippet(item.Overview));
        }

        user.Append('\n');
    }

    private static string Clean(string value) =>
        value.Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>Cleaned overview cut at a word boundary, so candidate lines stay compact.</summary>
    private static string Snippet(string overview)
    {
        var cleaned = Clean(overview);
        if (cleaned.Length <= SynopsisLength)
        {
            return cleaned;
        }

        var cut = cleaned[..SynopsisLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > SynopsisLength * 0.6 ? cut[..lastSpace] : cut) + "…";
    }
}
