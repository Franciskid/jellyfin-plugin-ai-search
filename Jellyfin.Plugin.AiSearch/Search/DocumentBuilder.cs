using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Turns a movie, series, or episode into the text that gets embedded: title,
/// genres, tags, director + lead cast, and the overview — so thematic queries
/// ("films about Native Americans", "the episode where they go to space") have
/// material to match beyond the title.
/// </summary>
public class DocumentBuilder
{
    private const int MaxTags = 16;
    private const int MaxActors = 6;

    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="DocumentBuilder"/> class.</summary>
    /// <param name="libraryManager">Library manager (people lookups).</param>
    public DocumentBuilder(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>Builds the embeddable text for one movie, series, or episode.</summary>
    /// <param name="item">The item.</param>
    /// <returns>A single paragraph describing the item.</returns>
    public string BuildText(BaseItem item)
    {
        if (item is Episode episode)
        {
            return BuildEpisodeText(episode);
        }

        var parts = new List<string>
        {
            item is Series
                ? (item.ProductionYear is > 0 ? $"TV series: {item.Name} ({item.ProductionYear})" : $"TV series: {item.Name}")
                : (item.ProductionYear is > 0 ? $"{item.Name} ({item.ProductionYear})" : item.Name),
        };

        if (item.Genres is { Length: > 0 })
        {
            parts.Add("Genres: " + string.Join(", ", item.Genres) + ".");
        }

        if (item.Tags is { Length: > 0 })
        {
            parts.Add("Tags: " + string.Join(", ", item.Tags.Take(MaxTags)) + ".");
        }

        var people = People(item);
        if (people.Count > 0)
        {
            parts.Add("Cast: " + string.Join(", ", people) + ".");
        }

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            parts.Add(item.Overview);
        }

        return string.Join(" ", parts);
    }

    /// <summary>A human label for an episode: "Series — S02E05 — Title".</summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The display label.</returns>
    public static string EpisodeLabel(Episode episode)
    {
        var code = episode.ParentIndexNumber is int s && episode.IndexNumber is int e
            ? $"S{s:D2}E{e:D2}"
            : null;
        var series = string.IsNullOrWhiteSpace(episode.SeriesName) ? null : episode.SeriesName;
        var title = string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name;
        return string.Join(" — ", new[] { series, code, title }.Where(p => p is not null));
    }

    private string BuildEpisodeText(Episode episode)
    {
        var parts = new List<string> { "TV episode: " + EpisodeLabel(episode) + "." };

        var genres = episode.Genres is { Length: > 0 } ? episode.Genres : episode.Series?.Genres;
        if (genres is { Length: > 0 })
        {
            parts.Add("Genres: " + string.Join(", ", genres) + ".");
        }

        var people = People(episode);
        if (people.Count > 0)
        {
            parts.Add("Cast: " + string.Join(", ", people) + ".");
        }

        if (!string.IsNullOrWhiteSpace(episode.Overview))
        {
            parts.Add(episode.Overview);
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Content hash for change detection. It covers the document text plus the
    /// model and document prefix, so switching either re-embeds everything on
    /// the next build — stale vectors can never linger.
    /// </summary>
    /// <param name="text">The document text from <see cref="BuildText"/>.</param>
    /// <param name="model">The embedding model id.</param>
    /// <param name="documentPrefix">The configured document prefix.</param>
    /// <returns>A hex SHA-256 digest.</returns>
    public static string ContentHash(string text, string model, string documentPrefix)
    {
        var bytes = Encoding.UTF8.GetBytes(model + "|" + documentPrefix + "|" + text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private List<string> People(BaseItem item)
    {
        // Directors first (strong topical signal), then billing-order actors.
        var directors = new List<string>();
        var actors = new List<string>();
        foreach (var person in _libraryManager.GetPeople(item))
        {
            if (string.IsNullOrWhiteSpace(person.Name))
            {
                continue;
            }

            if (person.Type == PersonKind.Director)
            {
                directors.Add(person.Name);
            }
            else if (person.Type == PersonKind.Actor && actors.Count < MaxActors)
            {
                actors.Add(person.Name);
            }
        }

        directors.AddRange(actors);
        return directors;
    }
}
