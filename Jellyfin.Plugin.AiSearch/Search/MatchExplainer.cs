using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>Why a meaning match showed up, as shown under its card.</summary>
/// <param name="Why">Short evidence line, e.g. "“shark” in the synopsis · genre Horror".</param>
/// <param name="Snippet">A synopsis excerpt, around the first matched word when there is one.</param>
public sealed record MatchExplanation(string Why, string? Snippet);

/// <summary>
/// Instant, model-free explanation for a meaning match: which words of the
/// search appear in the item's genres, tags, people, studios, series name or
/// synopsis (accent- and case-insensitive, with a loose prefix match so
/// "pirate" finds "pirates"). When nothing overlaps word for word, which is
/// common across languages, it says the match is by meaning and shows the
/// start of the synopsis so the user can judge. A language model can replace
/// this with a proper sentence afterwards (see the Explain endpoint).
/// </summary>
public static class MatchExplainer
{
    private const int SnippetLength = 140;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // English
        "the", "and", "for", "with", "that", "this", "from", "about", "movie", "movies", "film", "films", "show", "shows",
        "series", "episode", "episodes", "where", "when", "what", "who", "which", "some", "something", "like", "into", "over",
        "are", "was", "were", "has", "have", "had", "not", "but", "all", "any", "one", "his", "her", "their", "them", "they",
        // French
        "les", "des", "une", "dans", "avec", "pour", "sur", "par", "qui", "que", "quoi", "est", "sont", "son", "ses", "leur",
        "leurs", "aux", "pas", "plus", "comme", "serie", "quand", "chose", "truc", "genre", "avoir", "etre"
    };

    /// <summary>Builds the explanation for one match.</summary>
    /// <param name="item">The matched item.</param>
    /// <param name="term">The search term.</param>
    /// <param name="people">Names of the item's cast and crew.</param>
    /// <param name="french">Whether to phrase it in French.</param>
    /// <returns>The explanation.</returns>
    public static MatchExplanation Explain(BaseItem item, string term, IReadOnlyList<string> people, bool french)
    {
        var tokens = Tokens(term);
        var evidence = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        void Field(IEnumerable<string>? values, string labelEn, string labelFr)
        {
            if (values is null || evidence.Count >= 2)
            {
                return;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || used.Contains(value))
                {
                    continue;
                }

                var words = Tokens(value, keepStopWords: true);
                if (tokens.Any(t => words.Any(w => SameWord(t, w))))
                {
                    used.Add(value);
                    evidence.Add((french ? labelFr : labelEn) + " " + value);
                    return;
                }
            }
        }

        // A word of the search in the title itself (the whole term is not,
        // or Jellyfin's title search would have listed it already).
        var (titleWord, _) = FirstHit(item.Name ?? string.Empty, tokens);
        if (titleWord is not null)
        {
            evidence.Add(french ? "« " + titleWord + " » dans le titre" : "“" + titleWord + "” in the title");
        }

        Field(item.Genres, "genre", "genre");
        Field(item.Tags, "tag", "tag");
        Field(people, "with", "avec");
        Field(item.Studios, "studio", "studio");
        if (item is Episode episode)
        {
            Field(new[] { episode.SeriesName ?? string.Empty }, "series", "série");
        }

        string? snippet = null;
        // One line of text: line breaks and runs of spaces become single spaces.
        var overview = string.Join(' ', (item.Overview ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrWhiteSpace(overview))
        {
            var (hitWord, position) = FirstHit(overview, tokens);
            // The same word already cited from the title says nothing new.
            var sameAsTitle = hitWord is not null && titleWord is not null
                && string.Equals(TitleMatcher.Clean(TitleMatcher.RemoveDiacritics(hitWord)), TitleMatcher.Clean(TitleMatcher.RemoveDiacritics(titleWord)), StringComparison.Ordinal);
            if (hitWord is not null && !sameAsTitle && evidence.Count < 2)
            {
                evidence.Insert(0, french ? "« " + hitWord + " » dans le résumé" : "“" + hitWord + "” in the synopsis");
            }

            snippet = Excerpt(overview, position);
        }

        var why = evidence.Count > 0
            ? string.Join(" · ", evidence)
            : french ? "Proche par le sens de votre recherche" : "Close in meaning to your search";
        return new MatchExplanation(why, snippet);
    }

    private static List<string> Tokens(string text, bool keepStopWords = false)
    {
        var clean = TitleMatcher.Clean(TitleMatcher.RemoveDiacritics(text));
        return clean
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && (keepStopWords || !StopWords.Contains(w)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // Loose stemming: equal, or the shorter word starts the longer one with at
    // most three extra letters ("pirate"/"pirates", "chevalier"/"chevaliers"),
    // which avoids pairs like "requin"/"requiem".
    private static bool SameWord(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 4
            && longer.Length - shorter.Length <= 3
            && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static (string? Word, int Position) FirstHit(string overview, List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return (null, -1);
        }

        // Walk the original text word by word so the excerpt and the quoted
        // word keep their accents and case.
        var start = -1;
        for (var i = 0; i <= overview.Length; i++)
        {
            var isLetter = i < overview.Length && char.IsLetterOrDigit(overview[i]);
            if (isLetter && start < 0)
            {
                start = i;
            }
            else if (!isLetter && start >= 0)
            {
                var word = overview[start..i];
                var clean = TitleMatcher.Clean(TitleMatcher.RemoveDiacritics(word));
                if (clean.Length >= 3 && tokens.Any(t => SameWord(t, clean)))
                {
                    return (word, start);
                }

                start = -1;
            }
        }

        return (null, -1);
    }

    private static string Excerpt(string overview, int position)
    {
        var text = overview;
        if (text.Length <= SnippetLength)
        {
            return text;
        }

        if (position < 0 || position < SnippetLength / 2)
        {
            return Cut(text, 0);
        }

        var from = text.LastIndexOf(' ', Math.Max(0, position - (SnippetLength / 3)));
        return "…" + Cut(text, from < 0 ? 0 : from + 1);
    }

    private static string Cut(string text, int from)
    {
        if (text.Length - from <= SnippetLength)
        {
            return text[from..];
        }

        var end = text.LastIndexOf(' ', from + SnippetLength);
        if (end <= from)
        {
            end = from + SnippetLength;
        }

        return text[from..end].TrimEnd(',', ';', ':', '.') + "…";
    }
}
