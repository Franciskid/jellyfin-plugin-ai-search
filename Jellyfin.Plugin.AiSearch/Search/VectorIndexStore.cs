using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Persists the index as one small binary file in Jellyfin's data folder, so a
/// server restart never triggers a re-embed. Format v1:
/// magic "AISEARCH" · version byte · model · builtAt ticks · dims · count ·
/// then per entry: item guid · content hash · dims × float32.
/// </summary>
public class VectorIndexStore
{
    private const string Magic = "AISEARCH";
    private const byte Version = 1;

    private readonly string _path;
    private readonly ILogger<VectorIndexStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="VectorIndexStore"/> class.</summary>
    /// <param name="paths">Jellyfin application paths (for the data folder).</param>
    /// <param name="logger">Logger.</param>
    public VectorIndexStore(IApplicationPaths paths, ILogger<VectorIndexStore> logger)
    {
        _path = Path.Combine(paths.DataPath, "aisearch", "index.bin");
        _logger = logger;
    }

    /// <summary>Loads the persisted snapshot, or <c>null</c> when absent or unreadable.</summary>
    /// <returns>The snapshot, or <c>null</c>.</returns>
    public IndexSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = File.OpenRead(_path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!string.Equals(Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)), Magic, StringComparison.Ordinal)
                || reader.ReadByte() != Version)
            {
                _logger.LogWarning("AiSearch: index file has an unknown format; it will be rebuilt.");
                return null;
            }

            var model = reader.ReadString();
            var builtAt = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var dimensions = reader.ReadInt32();
            var count = reader.ReadInt32();
            var entries = new List<IndexEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var itemId = new Guid(reader.ReadBytes(16));
                var hash = reader.ReadString();
                var vector = new float[dimensions];
                for (var d = 0; d < dimensions; d++)
                {
                    vector[d] = reader.ReadSingle();
                }

                entries.Add(new IndexEntry(itemId, hash, vector));
            }

            _logger.LogInformation("AiSearch: loaded index of {Count} movies ({Model}, {Dims} dims).", count, model, dimensions);
            return new IndexSnapshot(model, dimensions, builtAt, entries);
        }
        catch (Exception ex)
        {
            // A corrupt file is not fatal — the next build simply recreates it.
            _logger.LogWarning(ex, "AiSearch: could not load the index file; it will be rebuilt.");
            return null;
        }
    }

    /// <summary>Atomically writes the snapshot (temp file + rename).</summary>
    /// <param name="snapshot">The snapshot to persist.</param>
    public void Save(IndexSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(Version);
            writer.Write(snapshot.Model);
            writer.Write(snapshot.BuiltAt.Ticks);
            writer.Write(snapshot.Dimensions);
            writer.Write(snapshot.Entries.Count);
            foreach (var entry in snapshot.Entries)
            {
                writer.Write(entry.ItemId.ToByteArray());
                writer.Write(entry.ContentHash);
                foreach (var value in entry.Vector)
                {
                    writer.Write(value);
                }
            }
        }

        File.Move(temp, _path, overwrite: true);
    }
}
