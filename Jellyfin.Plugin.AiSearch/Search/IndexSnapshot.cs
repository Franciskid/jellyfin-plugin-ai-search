using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>One indexed movie: its Jellyfin id, a content hash (to skip re-embedding unchanged metadata), and its unit-length vector.</summary>
/// <param name="ItemId">The Jellyfin item id.</param>
/// <param name="ContentHash">SHA-256 of the embedded document + model + prefix.</param>
/// <param name="Vector">The L2-normalized embedding.</param>
public sealed record IndexEntry(Guid ItemId, string ContentHash, float[] Vector);

/// <summary>
/// An immutable, fully-built index. The whole snapshot is swapped atomically on
/// rebuild, so readers never see a half-built index and no locking is needed.
/// </summary>
public sealed class IndexSnapshot
{
    /// <summary>Initializes a new instance of the <see cref="IndexSnapshot"/> class.</summary>
    /// <param name="model">The embedding model id the vectors were computed with.</param>
    /// <param name="dimensions">The vector dimensionality.</param>
    /// <param name="builtAt">When the snapshot finished building (UTC).</param>
    /// <param name="entries">The indexed movies.</param>
    public IndexSnapshot(string model, int dimensions, DateTime builtAt, IReadOnlyList<IndexEntry> entries)
    {
        Model = model;
        Dimensions = dimensions;
        BuiltAt = builtAt;
        Entries = entries;
    }

    /// <summary>Gets the embedding model id the vectors were computed with.</summary>
    public string Model { get; }

    /// <summary>Gets the vector dimensionality.</summary>
    public int Dimensions { get; }

    /// <summary>Gets the UTC build time.</summary>
    public DateTime BuiltAt { get; }

    /// <summary>Gets the indexed movies.</summary>
    public IReadOnlyList<IndexEntry> Entries { get; }
}
