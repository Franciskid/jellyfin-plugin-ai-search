using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Holds the current in-memory index and answers nearest-neighbour queries.
/// At library scale (tens of thousands of entries × ~1k dims) an exact
/// brute-force scan over one contiguous matrix with SIMD dot products takes a
/// few milliseconds, so no vector database is needed.
/// </summary>
public class VectorIndex
{
    // Below this many rows one thread finishes before a fan-out would pay off.
    private const int ParallelThresholdRows = 8192;
    private const int RowsPerChunk = 4096;

    private volatile IndexSnapshot? _current;

    /// <summary>Gets the current snapshot, or <c>null</c> before the first load/build.</summary>
    public IndexSnapshot? Current => _current;

    /// <summary>Atomically replaces the index with a freshly built snapshot.</summary>
    /// <param name="snapshot">The new snapshot.</param>
    public void Replace(IndexSnapshot snapshot) => _current = snapshot;

    /// <summary>
    /// Whether the index can serve queries for the given model, it exists,
    /// has entries, and was built with the same embedding model (vectors from
    /// different models live in incompatible spaces).
    /// </summary>
    /// <param name="model">The embedding model configured right now.</param>
    /// <returns><c>true</c> when queries against this index are meaningful.</returns>
    public bool IsUsable(string model)
    {
        var snapshot = _current;
        return snapshot is not null
            && snapshot.Count > 0
            && string.Equals(snapshot.Model, model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the <paramref name="take"/> most similar items, best first.</summary>
    /// <param name="normalizedQuery">The unit-length query vector.</param>
    /// <param name="take">How many hits to return.</param>
    /// <param name="allowedIds">
    /// Optional filter: when non-null and non-empty, only entries whose item id is
    /// in this set are scored, used to scope a query to a single series' episodes.
    /// </param>
    /// <returns>Item ids with their cosine similarity, descending.</returns>
    public IReadOnlyList<(Guid ItemId, float Score)> Search(float[] normalizedQuery, int take, HashSet<Guid>? allowedIds = null)
    {
        var snapshot = _current;
        if (snapshot is null || take <= 0 || snapshot.Count == 0 || snapshot.Dimensions != normalizedQuery.Length)
        {
            return Array.Empty<(Guid, float)>();
        }

        var filter = allowedIds is { Count: > 0 } ? allowedIds : null;
        var rows = snapshot.Count;
        if (rows < ParallelThresholdRows || Environment.ProcessorCount < 2)
        {
            return TopK.Drain(ScanRange(snapshot, normalizedQuery, take, filter, 0, rows));
        }

        // Each chunk keeps its own top-k; merging chunk winners afterwards
        // gives the exact global top-k without any shared mutable state.
        var chunks = (rows + RowsPerChunk - 1) / RowsPerChunk;
        var partials = new TopK[chunks];
        Parallel.For(0, chunks, chunk =>
        {
            var start = chunk * RowsPerChunk;
            partials[chunk] = ScanRange(snapshot, normalizedQuery, take, filter, start, Math.Min(rows, start + RowsPerChunk));
        });

        var merged = new TopK(take);
        foreach (var partial in partials)
        {
            merged.AddAll(partial);
        }

        return TopK.Drain(merged);
    }

    private static TopK ScanRange(IndexSnapshot snapshot, float[] query, int take, HashSet<Guid>? filter, int start, int end)
    {
        var dims = snapshot.Dimensions;
        var ids = snapshot.Ids;
        var matrix = snapshot.Matrix;
        var top = new TopK(take);
        for (var row = start; row < end; row++)
        {
            if (filter is not null && !filter.Contains(ids[row]))
            {
                continue;
            }

            top.Add(row, ids[row], VectorMath.Dot(query, matrix.Slice(row * dims, dims)));
        }

        return top;
    }

    /// <summary>
    /// Bounded min-heap keeping the best <c>k</c> scores seen, so a scan never
    /// sorts the whole index. Ties break on row order for stable results.
    /// </summary>
    private sealed class TopK
    {
        private readonly PriorityQueue<(int Row, Guid Id, float Score), (float Score, int NegRow)> _heap;
        private readonly int _capacity;

        public TopK(int capacity)
        {
            _capacity = capacity;
            _heap = new PriorityQueue<(int, Guid, float), (float, int)>(Math.Min(capacity, 1024) + 1, PriorityComparer.Instance);
        }

        public void Add(int row, Guid id, float score)
        {
            var priority = (score, -row);
            if (_heap.Count < _capacity)
            {
                _heap.Enqueue((row, id, score), priority);
            }
            else if (PriorityComparer.Instance.Compare(priority, PeekPriority()) > 0)
            {
                _heap.DequeueEnqueue((row, id, score), priority);
            }
        }

        public void AddAll(TopK other)
        {
            foreach (var (element, _) in other._heap.UnorderedItems)
            {
                Add(element.Row, element.Id, element.Score);
            }
        }

        public static List<(Guid ItemId, float Score)> Drain(TopK top)
        {
            var result = new List<(Guid ItemId, float Score)>(top._heap.Count);
            while (top._heap.Count > 0)
            {
                var element = top._heap.Dequeue();
                result.Add((element.Id, element.Score));
            }

            result.Reverse();
            return result;
        }

        private (float Score, int NegRow) PeekPriority()
        {
            _heap.TryPeek(out _, out var priority);
            return priority;
        }
    }

    private sealed class PriorityComparer : IComparer<(float Score, int NegRow)>
    {
        public static readonly PriorityComparer Instance = new();

        public int Compare((float Score, int NegRow) x, (float Score, int NegRow) y)
        {
            var byScore = x.Score.CompareTo(y.Score);
            return byScore != 0 ? byScore : x.NegRow.CompareTo(y.NegRow);
        }
    }
}
