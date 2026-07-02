using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Holds the current in-memory index and answers nearest-neighbour queries.
/// At movie-library scale (a few thousand entries × ~1k dims ≈ tens of MB,
/// a few ms per brute-force scan) no vector database is needed.
/// </summary>
public class VectorIndex
{
    private volatile IndexSnapshot? _current;

    /// <summary>Gets the current snapshot, or <c>null</c> before the first load/build.</summary>
    public IndexSnapshot? Current => _current;

    /// <summary>Atomically replaces the index with a freshly built snapshot.</summary>
    /// <param name="snapshot">The new snapshot.</param>
    public void Replace(IndexSnapshot snapshot) => _current = snapshot;

    /// <summary>
    /// Whether the index can serve queries for the given model — it exists,
    /// has entries, and was built with the same embedding model (vectors from
    /// different models live in incompatible spaces).
    /// </summary>
    /// <param name="model">The embedding model configured right now.</param>
    /// <returns><c>true</c> when queries against this index are meaningful.</returns>
    public bool IsUsable(string model)
    {
        var snapshot = _current;
        return snapshot is not null
            && snapshot.Entries.Count > 0
            && string.Equals(snapshot.Model, model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the <paramref name="take"/> most similar items, best first.</summary>
    /// <param name="normalizedQuery">The unit-length query vector.</param>
    /// <param name="take">How many hits to return.</param>
    /// <returns>Item ids with their cosine similarity, descending.</returns>
    public IReadOnlyList<(Guid ItemId, float Score)> Search(float[] normalizedQuery, int take)
    {
        var snapshot = _current;
        if (snapshot is null || snapshot.Dimensions != normalizedQuery.Length || snapshot.Entries.Count == 0)
        {
            return Array.Empty<(Guid, float)>();
        }

        var scored = new List<(Guid ItemId, float Score)>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            scored.Add((entry.ItemId, VectorMath.Dot(normalizedQuery, entry.Vector)));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count > take)
        {
            scored.RemoveRange(take, scored.Count - take);
        }

        return scored;
    }
}
