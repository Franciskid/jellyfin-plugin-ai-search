using System.Collections.Generic;
using System.Text;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// Builds the system + user messages for a direct-mode recommendation. The
/// model only ever picks by index from the CANDIDATES list, which keeps it
/// from inventing titles that are not in the library.
/// </summary>
public static class PromptBuilder
{
    /// <summary>Builds the chat messages for an OpenAI-compatible completion call.</summary>
    /// <param name="prompt">The user's request.</param>
    /// <param name="locale">"fr" or "en" — the language for answer and reasons.</param>
    /// <param name="maxResults">How many recommendations to ask for.</param>
    /// <param name="candidates">The movies the model may choose from.</param>
    /// <param name="favorites">Title (year) list of the user's favorites, as a taste signal.</param>
    /// <param name="watched">Title (year) list of watched movies, as a taste signal.</param>
    /// <param name="includeDetails">
    /// Whether to append cast and a short overview per candidate. On for
    /// semantic retrieval (≤ ~60 rich candidates); off for the legacy catalog
    /// dump, where hundreds of detail lines would explode the token count.
    /// </param>
    /// <param name="synopsisLength">
    /// Maximum synopsis length (chars) per candidate. 0 or less means no
    /// limit — the full synopsis is sent uncropped.
    /// </param>
    /// <returns>The messages array for the completions payload.</returns>
    public static object[] BuildMessages(
        string prompt,
        string locale,
        int maxResults,
        IReadOnlyList<CandidateMovie> candidates,
        List<string> favorites,
        List<string> watched,
        bool includeDetails,
        int synopsisLength,
        string? tasteProfile = null)
    {
        var language = locale == "fr"
            ? "Write the \"answer\" and every \"reason\" in French. "
            : "Write the \"answer\" and every \"reason\" in English. ";
        var system =
            "You are a film recommendation engine for a personal Jellyfin movie library. " +
            "Recommend ONLY movies from the provided CANDIDATES list, chosen to match the user's request and taste. " +
            "Never invent titles or indices that are not in the list. Judge each candidate against the request " +
            "using ONLY the provided fields (cast, genres, synopsis), not your own memory of the film. " +
            "Include a movie ONLY if it genuinely satisfies the request; never include one that fails a stated " +
            "constraint (e.g. an actor, year, or genre the user asked for), and never list a movie just to explain " +
            "why it was excluded. If only one movie fits, return exactly one; if none fit, return an empty list. " +
            "When many genuinely fit, return up to " + maxResults + " for variety. " + language +
            "Respond with STRICT minified JSON only (no markdown, no prose) of exactly this shape: " +
            "{\"answer\":\"one short sentence\",\"recommendations\":[{\"index\":<integer from the list>,\"reason\":\"<=140 chars why it fits\"}]}.";

        var user = new StringBuilder();
        user.Append("REQUEST: ").Append(prompt).Append('\n');
        if (!string.IsNullOrWhiteSpace(tasteProfile))
        {
            user.Append("USER_TASTE_PROFILE: ").Append(tasteProfile.Trim()).Append('\n');
        }

        if (favorites.Count > 0)
        {
            user.Append("USER_FAVORITES: ").Append(string.Join("; ", favorites)).Append('\n');
        }

        if (watched.Count > 0)
        {
            user.Append("USER_WATCHED: ").Append(string.Join("; ", watched)).Append('\n');
        }

        user.Append(includeDetails
            ? "CANDIDATES (index|title (year)|genres|cast|synopsis):\n"
            : "CANDIDATES (index|title (year)|genres):\n");
        for (var i = 0; i < candidates.Count; i++)
        {
            AppendCandidateLine(user, i, candidates[i], includeDetails, synopsisLength);
        }

        return new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user.ToString() },
        };
    }

    /// <summary>
    /// Builds the messages that ask the model to propose a few short
    /// multiple-choice questions tailored to the user's initial request, so the
    /// "Help me choose" flow can refine a vague prompt instead of asking the
    /// generic mood/time/seen questions.
    /// </summary>
    /// <param name="prompt">The user's initial request.</param>
    /// <param name="locale">"fr" or "en" — the language for the questions.</param>
    /// <returns>The messages array for the completions payload.</returns>
    public static object[] BuildInterviewMessages(string prompt, string locale)
    {
        var language = locale == "fr" ? "French" : "English";
        var system =
            "You help a user refine a movie search of their personal library. " +
            "Given their initial request, produce 2 or 3 short multiple-choice questions whose answers would most " +
            "help narrow down a great recommendation. Ask ONLY about dimensions the request leaves open (for example " +
            "tone, era, pace, length, language, or how familiar they want it) — never re-ask something the request " +
            "already states. Each question must have between 2 and 5 concise options (a few words each). " +
            "Write every question and option in " + language + ". " +
            "Respond with STRICT minified JSON only (no markdown, no prose) of exactly this shape: " +
            "{\"questions\":[{\"label\":\"<the question>\",\"options\":[\"<option>\",\"<option>\"]}]}.";

        return new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = "INITIAL REQUEST: " + prompt },
        };
    }

    /// <summary>
    /// Builds the messages that ask the model to distill a viewer's taste into a
    /// short profile from their favorites and most-watched titles. Kept silent
    /// server-side and fed back into future searches.
    /// </summary>
    /// <param name="favorites">Title (year) list of favorites.</param>
    /// <param name="watched">Title (year) list of watched titles.</param>
    /// <param name="locale">"fr" or "en" — the language for the summary.</param>
    /// <returns>The messages array for the completions payload.</returns>
    public static object[] BuildTasteProfileMessages(List<string> favorites, List<string> watched, string locale)
    {
        var language = locale == "fr" ? "French" : "English";
        var system =
            "You profile a film & TV viewer's taste for a recommendation engine. " +
            "From their favorites and most-watched titles, write 2 to 4 sentences capturing what they seem to enjoy: " +
            "genres, tones, eras, pacing, languages, and any directors or themes that recur. Be specific and concrete, " +
            "not generic. Do not list the titles back; describe the taste behind them. Write in " + language + ". " +
            "Output ONLY the summary as plain prose — no JSON, no quotes, no field names, no code fences, no preamble.";

        var user = new StringBuilder();
        if (favorites.Count > 0)
        {
            user.Append("FAVORITES: ").Append(string.Join("; ", favorites)).Append('\n');
        }

        if (watched.Count > 0)
        {
            user.Append("WATCHED: ").Append(string.Join("; ", watched)).Append('\n');
        }

        return new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user.ToString() },
        };
    }

    private static void AppendCandidateLine(StringBuilder user, int index, CandidateMovie candidate, bool includeDetails, int synopsisLength)
    {
        var item = candidate.Item;
        if (item is Episode episode)
        {
            // Episodes already carry series + SxxExx + title in their label.
            user.Append(index).Append('|').Append(Clean(DocumentBuilder.EpisodeLabel(episode)));
        }
        else
        {
            user.Append(index).Append('|').Append(Clean(item.Name));
            if (item.ProductionYear is > 0)
            {
                user.Append(" (").Append(item.ProductionYear).Append(')');
            }
        }

        if (item.Genres is { Length: > 0 })
        {
            user.Append('|').Append(string.Join(',', item.Genres[..System.Math.Min(4, item.Genres.Length)]));
        }

        if (includeDetails)
        {
            // Cast is the signal that answers people queries; keep the column
            // even when empty so the pipe positions stay aligned.
            user.Append('|').Append(Clean(candidate.Cast));
            if (!string.IsNullOrWhiteSpace(item.Overview))
            {
                user.Append('|').Append(Snippet(item.Overview, synopsisLength));
            }
        }

        user.Append('\n');
    }

    private static string Clean(string value) =>
        value.Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>Cleaned overview cut at a word boundary, so candidate lines stay compact.</summary>
    private static string Snippet(string overview, int synopsisLength)
    {
        var cleaned = Clean(overview);
        if (synopsisLength <= 0 || cleaned.Length <= synopsisLength)
        {
            return cleaned;
        }

        var cut = cleaned[..synopsisLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > synopsisLength * 0.6 ? cut[..lastSpace] : cut) + "…";
    }
}
