using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiSearch.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Knows which titles live in libraries the admin excluded from AI Search
/// (<see cref="PluginConfiguration.ExcludedLibraryIds"/>), so every path can
/// drop them: the index build, the AI popup, and the meaning matches added to
/// Jellyfin's search. The id set comes from one database query and is reused
/// for a couple of minutes, so a search never pays for it.
/// </summary>
public sealed class LibraryScope
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private static readonly BaseItemKind[] Kinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };

    private readonly ILibraryManager _libraryManager;
    private readonly object _gate = new();
    private string _cachedKey = string.Empty;
    private DateTime _cachedAt;
    private IReadOnlySet<Guid> _cached = new HashSet<Guid>();

    /// <summary>Initializes a new instance of the <see cref="LibraryScope"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    public LibraryScope(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>The excluded library ids from the configuration, ignoring anything unparsable.</summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>The excluded library (collection folder) ids.</returns>
    public static Guid[] ExcludedLibraries(PluginConfiguration config) =>
        (config.ExcludedLibraryIds ?? Array.Empty<string>())
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

    /// <summary>
    /// The ids of every movie, series and episode in an excluded library. Empty
    /// when nothing is excluded.
    /// </summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <returns>The ids to leave out.</returns>
    public IReadOnlySet<Guid> ExcludedItemIds(PluginConfiguration config)
    {
        var libraries = ExcludedLibraries(config);
        if (libraries.Length == 0)
        {
            return new HashSet<Guid>();
        }

        var key = string.Join(',', libraries.OrderBy(id => id));
        lock (_gate)
        {
            if (key == _cachedKey && DateTime.UtcNow - _cachedAt < CacheFor)
            {
                return _cached;
            }
        }

        // Titles record the library's physical folders as their top parent,
        // not the library (collection folder) itself, so map one to the other.
        var topParents = new HashSet<Guid>(libraries);
        foreach (var library in libraries)
        {
            if (_libraryManager.GetItemById(library) is CollectionFolder folder)
            {
                topParents.UnionWith(folder.PhysicalFolderIds);
            }
        }

        var ids = new HashSet<Guid>(_libraryManager.GetItemIds(new InternalItemsQuery
        {
            TopParentIds = topParents.ToArray(),
            IncludeItemTypes = Kinds,
            Recursive = true
        }));

        lock (_gate)
        {
            _cachedKey = key;
            _cachedAt = DateTime.UtcNow;
            _cached = ids;
        }

        return ids;
    }

    /// <summary>Removes items of excluded libraries from a list.</summary>
    /// <param name="config">The current plugin configuration.</param>
    /// <param name="items">The items to filter.</param>
    /// <returns>The items outside excluded libraries (the same list when nothing is excluded).</returns>
    public IReadOnlyList<BaseItem> Filter(PluginConfiguration config, IReadOnlyList<BaseItem> items)
    {
        var excluded = ExcludedItemIds(config);
        return excluded.Count == 0 ? items : items.Where(item => !excluded.Contains(item.Id)).ToList();
    }
}
