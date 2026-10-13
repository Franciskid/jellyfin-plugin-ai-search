using System;
using System.Globalization;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Tells whether Jellyfin's own title search would also have found an item,
/// so a semantic hit can be labelled by where it really came from. Mirrors
/// the built-in <c>SqlSearchProvider</c>: the item's clean name (lowercase,
/// no diacritics, punctuation turned into spaces) contains the clean term, or
/// its original title contains the raw term. Re-implemented here rather than
/// calling Jellyfin's string helpers, which are not present in every 12.x
/// release the plugin loads on.
/// </summary>
public static class TitleMatcher
{
    /// <summary>Whether the built-in title search matches <paramref name="item"/> for <paramref name="searchTerm"/>.</summary>
    /// <param name="item">The candidate item.</param>
    /// <param name="searchTerm">The raw search term.</param>
    /// <returns><c>true</c> when the item is a title match.</returns>
    public static bool IsTitleMatch(BaseItem item, string searchTerm)
    {
        var raw = RemoveDiacritics(searchTerm.Trim());
        var clean = Clean(raw);
        if (clean.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(item.Name) && Clean(RemoveDiacritics(item.Name)).Contains(clean, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(item.OriginalTitle)
            && RemoveDiacritics(item.OriginalTitle).Contains(raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lowercases, drops punctuation and collapses whitespace, like Jellyfin's clean names.</summary>
    /// <param name="value">A diacritic-free string.</param>
    /// <returns>The clean form.</returns>
    internal static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(c);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Strips combining marks ("é" becomes "e").</summary>
    /// <param name="value">The input.</param>
    /// <returns>The input without diacritics.</returns>
    internal static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
