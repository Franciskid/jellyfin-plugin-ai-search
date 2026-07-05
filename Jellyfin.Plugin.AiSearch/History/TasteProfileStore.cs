using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.History;

/// <summary>A distilled, model-written summary of one user's film/TV taste.</summary>
public sealed class TasteProfile
{
    /// <summary>Gets or sets the 2-3 sentence taste summary.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was generated (ISO-8601 UTC).</summary>
    public string BuiltAt { get; set; } = string.Empty;

    /// <summary>Gets or sets the favorite count it was built from (drift detection).</summary>
    public int FavCount { get; set; }

    /// <summary>Gets or sets the watched count it was built from (drift detection).</summary>
    public int WatchedCount { get; set; }
}

/// <summary>
/// Per-user taste profiles, one small JSON file each under the plugin data
/// folder (<c>aisearch/taste/{userId}.json</c>). Written by a background refresh
/// after searches; read to enrich the prompt. Mirrors <see cref="HistoryStore"/>.
/// </summary>
public sealed class TasteProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly ILogger<TasteProfileStore> _logger;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="TasteProfileStore"/> class.</summary>
    /// <param name="paths">Jellyfin application paths (for the data folder).</param>
    /// <param name="logger">Logger.</param>
    public TasteProfileStore(IApplicationPaths paths, ILogger<TasteProfileStore> logger)
    {
        _dir = Path.Combine(paths.DataPath, "aisearch", "taste");
        _logger = logger;
    }

    /// <summary>Loads the user's profile, or <c>null</c> when absent/unreadable.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The profile, or <c>null</c>.</returns>
    public TasteProfile? Load(Guid userId)
    {
        lock (_gate)
        {
            var path = PathFor(userId);
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<TasteProfile>(File.ReadAllText(path), Json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AiSearch: could not read taste profile for {User}.", userId);
                return null;
            }
        }
    }

    /// <summary>Atomically writes the user's profile.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="profile">The profile to store.</param>
    public void Save(Guid userId, TasteProfile profile)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var path = PathFor(userId);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(profile, Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AiSearch: could not write taste profile for {User}.", userId);
            }
        }
    }

    private string PathFor(Guid userId) => Path.Combine(_dir, userId.ToString("N") + ".json");
}
