using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// Formats a movie's people into a compact line for the model prompt, e.g.
/// "Leonardo DiCaprio as Rick Dalton, Brad Pitt as Cliff Booth; dir. Quentin
/// Tarantino". Actors carry their character (role) when Jellyfin has it.
/// </summary>
public static class CastFormatter
{
    private const int MaxActors = 8;
    private const int MaxCrew = 2;

    /// <summary>Builds the cast/crew string, or empty when there are no named people.</summary>
    /// <param name="people">The item's people (from <c>ILibraryManager.GetPeople</c>).</param>
    /// <returns>A single compact line, or an empty string.</returns>
    public static string Format(IReadOnlyList<PersonInfo> people)
    {
        var actors = people
            .Where(p => p.Type == PersonKind.Actor && !string.IsNullOrWhiteSpace(p.Name))
            .Take(MaxActors)
            .Select(p => string.IsNullOrWhiteSpace(p.Role) ? p.Name : p.Name + " as " + p.Role);
        var directors = Names(people, PersonKind.Director);
        var writers = Names(people, PersonKind.Writer);

        var parts = new List<string>();
        var actorList = string.Join(", ", actors);
        if (actorList.Length > 0)
        {
            parts.Add(actorList);
        }

        if (directors.Count > 0)
        {
            parts.Add("dir. " + string.Join(", ", directors));
        }

        if (writers.Count > 0)
        {
            parts.Add("writ. " + string.Join(", ", writers));
        }

        return string.Join("; ", parts);
    }

    private static List<string> Names(IReadOnlyList<PersonInfo> people, PersonKind kind) => people
        .Where(p => p.Type == kind && !string.IsNullOrWhiteSpace(p.Name))
        .Select(p => p.Name)
        .Take(MaxCrew)
        .ToList();
}
