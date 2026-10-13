using System;
using System.IO;
using System.Runtime.InteropServices;
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
            if (dimensions < 0 || count < 0 || (long)dimensions * count > int.MaxValue)
            {
                _logger.LogWarning("AiSearch: index file header is invalid; it will be rebuilt.");
                return null;
            }

            // Each row's floats go straight into the shared matrix as raw bytes
            // instead of one ReadSingle call per value (millions of calls).
            var ids = new Guid[count];
            var hashes = new string[count];
            var matrix = new float[count * dimensions];
            Span<byte> guidBytes = stackalloc byte[16];
            for (var i = 0; i < count; i++)
            {
                ReadExactly(reader, guidBytes);
                ids[i] = new Guid(guidBytes);
                hashes[i] = reader.ReadString();
                var row = matrix.AsSpan(i * dimensions, dimensions);
                ReadExactly(reader, MemoryMarshal.AsBytes(row));
                if (!BitConverter.IsLittleEndian)
                {
                    ReverseFloatBytes(row);
                }
            }

            _logger.LogInformation("AiSearch: loaded index of {Count} items ({Model}, {Dims} dims).", count, model, dimensions);
            return new IndexSnapshot(model, dimensions, builtAt, ids, hashes, matrix);
        }
        catch (Exception ex)
        {
            // A corrupt file is not fatal, the next build simply recreates it.
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
                if (BitConverter.IsLittleEndian)
                {
                    writer.Write(MemoryMarshal.AsBytes(entry.Vector.Span));
                }
                else
                {
                    foreach (var value in entry.Vector.Span)
                    {
                        writer.Write(value);
                    }
                }
            }
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static void ReadExactly(BinaryReader reader, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var read = reader.Read(buffer);
            if (read <= 0)
            {
                throw new EndOfStreamException("Index file is truncated.");
            }

            buffer = buffer[read..];
        }
    }

    // The file is little-endian (what BinaryWriter writes); only a big-endian
    // host needs to swap the bulk-read bytes back into native floats.
    private static void ReverseFloatBytes(Span<float> values)
    {
        var bytes = MemoryMarshal.AsBytes(values);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes.Slice(i, 4).Reverse();
        }
    }
}
