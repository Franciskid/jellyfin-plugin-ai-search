using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>One indexed movie: its Jellyfin id, a content hash (to skip re-embedding unchanged metadata), and its unit-length vector.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="ContentHash">SHA-256 of the embedded document + model + prefix.</param>
/// <param name="Vector">The L2-normalized embedding (a slice of the snapshot's matrix once the entry belongs to one).</param>
public sealed record IndexEntry(Guid ItemId, string ContentHash, ReadOnlyMemory<float> Vector);

/// <summary>
/// An immutable, fully-built index. The whole snapshot is swapped atomically on
/// rebuild, so readers never see a half-built index and no locking is needed.
/// All vectors live in one contiguous row-major matrix, so a query scans a
/// single array front to back instead of chasing one heap object per entry.
/// </summary>
public sealed class IndexSnapshot
{
    private readonly float[] _matrix;
    private readonly Guid[] _ids;

    /// <summary>Initializes a new instance of the <see cref="IndexSnapshot"/> class.</summary>
    /// <param name="model">The embedding model id the vectors were computed with.</param>
    /// <param name="dimensions">The vector dimensionality.</param>
    /// <param name="builtAt">When the snapshot finished building (UTC).</param>
    /// <param name="entries">The indexed movies; their vectors are copied into the snapshot's matrix.</param>
    public IndexSnapshot(string model, int dimensions, DateTime builtAt, IReadOnlyList<IndexEntry> entries)
    {
        var usable = new List<IndexEntry>(entries.Count);
        foreach (var entry in entries)
        {
            // A vector of another size cannot be scored against this index; it
            // would only show up after a model swap mid-build, so drop it.
            if (entry.Vector.Length == dimensions)
            {
                usable.Add(entry);
            }
        }

        var hashes = new string[usable.Count];
        _ids = new Guid[usable.Count];
        _matrix = new float[usable.Count * dimensions];
        for (var i = 0; i < usable.Count; i++)
        {
            _ids[i] = usable[i].ItemId;
            hashes[i] = usable[i].ContentHash;
            usable[i].Vector.Span.CopyTo(_matrix.AsSpan(i * dimensions, dimensions));
        }

        Model = model;
        Dimensions = dimensions;
        BuiltAt = builtAt;
        Entries = BuildEntries(_ids, hashes, _matrix, dimensions);
    }

    /// <summary>Initializes a new instance of the <see cref="IndexSnapshot"/> class over an already-filled matrix (no copy).</summary>
    /// <param name="model">The embedding model id the vectors were computed with.</param>
    /// <param name="dimensions">The vector dimensionality.</param>
    /// <param name="builtAt">When the snapshot finished building (UTC).</param>
    /// <param name="ids">Item ids, one per matrix row.</param>
    /// <param name="hashes">Content hashes, one per matrix row.</param>
    /// <param name="matrix">Row-major vectors, <c>ids.Length * dimensions</c> floats.</param>
    internal IndexSnapshot(string model, int dimensions, DateTime builtAt, Guid[] ids, string[] hashes, float[] matrix)
    {
        if (matrix.Length != ids.Length * dimensions || hashes.Length != ids.Length)
        {
            throw new ArgumentException("Index matrix does not match its ids.", nameof(matrix));
        }

        _ids = ids;
        _matrix = matrix;
        Model = model;
        Dimensions = dimensions;
        BuiltAt = builtAt;
        Entries = BuildEntries(ids, hashes, matrix, dimensions);
    }

    /// <summary>Gets the embedding model id the vectors were computed with.</summary>
    public string Model { get; }

    /// <summary>Gets the vector dimensionality.</summary>
    public int Dimensions { get; }

    /// <summary>Gets the UTC build time.</summary>
    public DateTime BuiltAt { get; }

    /// <summary>Gets the indexed movies (their vectors are views into the shared matrix).</summary>
    public IReadOnlyList<IndexEntry> Entries { get; }

    /// <summary>Gets the number of indexed rows.</summary>
    public int Count => _ids.Length;

    /// <summary>Gets the item ids, one per matrix row.</summary>
    internal ReadOnlySpan<Guid> Ids => _ids;

    /// <summary>Gets the row-major vector matrix.</summary>
    internal ReadOnlySpan<float> Matrix => _matrix;

    private static IndexEntry[] BuildEntries(Guid[] ids, string[] hashes, float[] matrix, int dimensions)
    {
        var entries = new IndexEntry[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            entries[i] = new IndexEntry(ids[i], hashes[i], new ReadOnlyMemory<float>(matrix, i * dimensions, dimensions));
        }

        return entries;
    }
}
