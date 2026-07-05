using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>Outcome of one index build, shown on the config page.</summary>
/// <param name="StartedAt">Build start (UTC).</param>
/// <param name="FinishedAt">Build end (UTC).</param>
/// <param name="Embedded">Movies (re-)embedded this run.</param>
/// <param name="Reused">Movies kept from the previous index (unchanged).</param>
/// <param name="Total">Movies in the resulting index.</param>
/// <param name="Error">Error message, or <c>null</c> on success.</param>
public sealed record IndexBuildResult(DateTime StartedAt, DateTime FinishedAt, int Embedded, int Reused, int Total, string? Error);

/// <summary>
/// Builds the semantic index: walk the movie library, embed what changed
/// (content-hash), keep what didn't, then swap the in-memory index and persist
/// it. Single-flight — a second build request while one runs is a no-op.
/// </summary>
public class IndexBuilder
{
    private const int EmbedBatchSize = 16;

    private readonly ILibraryManager _libraryManager;
    private readonly DocumentBuilder _documents;
    private readonly EmbeddingsClient _embeddings;
    private readonly VectorIndex _index;
    private readonly VectorIndexStore _store;
    private readonly ILogger<IndexBuilder> _logger;
    private int _building;

    /// <summary>Initializes a new instance of the <see cref="IndexBuilder"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="documents">Document builder.</param>
    /// <param name="embeddings">Embeddings client.</param>
    /// <param name="index">The in-memory index to swap on success.</param>
    /// <param name="store">The on-disk persistence.</param>
    /// <param name="logger">Logger.</param>
    public IndexBuilder(
        ILibraryManager libraryManager,
        DocumentBuilder documents,
        EmbeddingsClient embeddings,
        VectorIndex index,
        VectorIndexStore store,
        ILogger<IndexBuilder> logger)
    {
        _libraryManager = libraryManager;
        _documents = documents;
        _embeddings = embeddings;
        _index = index;
        _store = store;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether a build is currently running.</summary>
    public bool IsBuilding => _building == 1;

    /// <summary>Gets the outcome of the most recent build, if any.</summary>
    public IndexBuildResult? LastRun { get; private set; }

    /// <summary>
    /// Runs one build. Returns immediately (with the previous result) when a
    /// build is already in flight or semantic search is not configured.
    /// </summary>
    /// <param name="progress">Optional progress sink (0–100), used by the scheduled task.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build outcome.</returns>
    public async Task<IndexBuildResult?> BuildAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null)
        {
            _logger.LogInformation("AiSearch: semantic index build skipped — no embedding model/endpoint configured.");
            return LastRun;
        }

        if (Interlocked.CompareExchange(ref _building, 1, 0) != 0)
        {
            return LastRun;
        }

        var startedAt = DateTime.UtcNow;
        try
        {
            var result = await RunBuildAsync(target, config.EmbeddingDocumentPrefix ?? string.Empty, startedAt, progress, cancellationToken)
                .ConfigureAwait(false);
            LastRun = result;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AiSearch: semantic index build failed.");
            LastRun = new IndexBuildResult(startedAt, DateTime.UtcNow, 0, 0, _index.Current?.Entries.Count ?? 0, ex.Message);
            return LastRun;
        }
        finally
        {
            Interlocked.Exchange(ref _building, 0);
        }
    }

    private async Task<IndexBuildResult> RunBuildAsync(
        EmbeddingsTarget target,
        string documentPrefix,
        DateTime startedAt,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var kinds = Plugin.Instance!.Configuration.IndexTvShows
            ? new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
            : new[] { BaseItemKind.Movie };
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
        }).Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToList();

        // Reuse vectors whose document (and model/prefix) did not change.
        var previous = ReusableEntries(target.Model);
        var entries = new List<IndexEntry>(items.Count);
        var pending = new List<(BaseItem Item, string Hash, string Text)>();
        foreach (var item in items)
        {
            var text = _documents.BuildText(item);
            var hash = DocumentBuilder.ContentHash(text, target.Model, documentPrefix);
            if (previous.TryGetValue(item.Id, out var entry) && string.Equals(entry.ContentHash, hash, StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
            else
            {
                pending.Add((item, hash, documentPrefix + text));
            }
        }

        var dimensions = entries.Count > 0 ? entries[0].Vector.Length : 0;
        for (var offset = 0; offset < pending.Count; offset += EmbedBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.GetRange(offset, Math.Min(EmbedBatchSize, pending.Count - offset));
            var vectors = await _embeddings
                .EmbedAsync(target, batch.ConvertAll(entry => entry.Text), cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                VectorMath.NormalizeInPlace(vectors[i]);
                dimensions = vectors[i].Length;
                entries.Add(new IndexEntry(batch[i].Item.Id, batch[i].Hash, vectors[i]));
            }

            progress?.Report(100.0 * Math.Min(offset + batch.Count, pending.Count) / Math.Max(1, pending.Count));
        }

        var snapshot = new IndexSnapshot(target.Model, dimensions, DateTime.UtcNow, entries);
        _index.Replace(snapshot);
        _store.Save(snapshot);
        _logger.LogInformation(
            "AiSearch: semantic index built — {Embedded} embedded, {Reused} reused, {Total} total ({Model}).",
            pending.Count,
            entries.Count - pending.Count,
            entries.Count,
            target.Model);
        progress?.Report(100);
        return new IndexBuildResult(startedAt, DateTime.UtcNow, pending.Count, entries.Count - pending.Count, entries.Count, null);
    }

    private Dictionary<Guid, IndexEntry> ReusableEntries(string model)
    {
        var current = _index.Current;
        if (current is null || !string.Equals(current.Model, model, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<Guid, IndexEntry>();
        }

        var byId = new Dictionary<Guid, IndexEntry>(current.Entries.Count);
        foreach (var entry in current.Entries)
        {
            byId.TryAdd(entry.ItemId, entry);
        }

        return byId;
    }
}
