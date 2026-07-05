using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.History;

/// <summary>One recommended movie as stored in a user's search history.</summary>
public sealed class HistoryItem
{
    /// <summary>Gets or sets the Jellyfin item id ("N" format).</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the movie title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the model's one-line reason.</summary>
    public string? Reason { get; set; }
}

/// <summary>One past search: the prompt, mode, and the movies it returned.</summary>
public sealed class HistoryEntry
{
    /// <summary>Gets or sets the opaque entry id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO-8601 UTC timestamp.</summary>
    public string At { get; set; } = string.Empty;

    /// <summary>Gets or sets the natural-language prompt (empty for surprise).</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the model's one-line answer sentence for this search, shown as the header when the entry is reopened.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>Gets or sets the mode: "normal" or "surprise".</summary>
    public string Mode { get; set; } = "normal";

    /// <summary>Gets or sets the number of movies returned.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets the returned movies (for the history sneak-peek and replay).</summary>
    public List<HistoryItem> Items { get; set; } = new();
}

/// <summary>
/// Per-user search history, persisted as one small JSON file per user under
/// the plugin's data folder (<c>aisearch/history/{userId}.json</c>). Newest
/// first, capped, and mutated under a lock so concurrent requests stay safe.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 12;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly ILogger<HistoryStore> _logger;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="HistoryStore"/> class.</summary>
    /// <param name="paths">Jellyfin application paths (for the data folder).</param>
    /// <param name="logger">Logger.</param>
    public HistoryStore(IApplicationPaths paths, ILogger<HistoryStore> logger)
    {
        _dir = Path.Combine(paths.DataPath, "aisearch", "history");
        _logger = logger;
    }

    /// <summary>Returns the user's history, newest first.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The stored entries.</returns>
    public IReadOnlyList<HistoryEntry> Load(Guid userId)
    {
        lock (_gate)
        {
            return LoadUnlocked(userId);
        }
    }

    /// <summary>Prepends an entry, trimming to the newest <see cref="MaxEntries"/>.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="entry">The entry to store.</param>
    public void Add(Guid userId, HistoryEntry entry)
    {
        lock (_gate)
        {
            var list = LoadUnlocked(userId);
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
            {
                list = list.Take(MaxEntries).ToList();
            }

            Save(userId, list);
        }
    }

    /// <summary>Appends items to the newest entry matching the prompt+mode, de-duplicating by item id.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="prompt">The prompt of the entry to append to.</param>
    /// <param name="mode">The mode of the entry to append to.</param>
    /// <param name="newItems">The items to append.</param>
    public void AppendToLatest(Guid userId, string prompt, string mode, IReadOnlyList<HistoryItem> newItems)
    {
        if (newItems is null || newItems.Count == 0) { return; }
        lock (_gate)
        {
            var list = LoadUnlocked(userId);
            var entry = list.FirstOrDefault(e =>
                string.Equals(e.Mode, mode, StringComparison.Ordinal) &&
                string.Equals(e.Prompt, prompt, StringComparison.Ordinal));
            if (entry is null) { return; }
            var existing = new HashSet<string>(entry.Items.Select(i => i.ItemId), StringComparer.Ordinal);
            foreach (var it in newItems)
            {
                if (!string.IsNullOrEmpty(it.ItemId) && existing.Add(it.ItemId)) { entry.Items.Add(it); }
            }

            entry.Count = entry.Items.Count;
            Save(userId, list);
        }
    }

    /// <summary>Deletes one entry, or the whole file when <paramref name="id"/> is null/empty.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="id">The entry id, or null to clear all.</param>
    public void Delete(Guid userId, string? id)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(id))
            {
                try
                {
                    var path = PathFor(userId);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AiSearch: could not clear history.");
                }

                return;
            }

            var list = LoadUnlocked(userId)
                .Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal))
                .ToList();
            Save(userId, list);
        }
    }

    private List<HistoryEntry> LoadUnlocked(Guid userId)
    {
        var path = PathFor(userId);
        try
        {
            if (!File.Exists(path))
            {
                return new List<HistoryEntry>();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json, Json) ?? new List<HistoryEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not read history for {User}.", userId);
            return new List<HistoryEntry>();
        }
    }

    private void Save(Guid userId, List<HistoryEntry> list)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(userId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not write history for {User}.", userId);
        }
    }

    private string PathFor(Guid userId) => Path.Combine(_dir, userId.ToString("N") + ".json");
}
