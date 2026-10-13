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
/// it. Single-flight, a second build request while one runs is a no-op.
/// </summary>
public class IndexBuilder
{
    private const int EmbedBatchSize = 16;

    private readonly ILibraryManager _libraryManager;
    private readonly DocumentBuilder _documents;
    private readonly EmbeddingsClient _embeddings;
    private readonly VectorIndex _index;
    private readonly VectorIndexStore _store;
    private readonly LibraryScope _scope;
    private readonly ILogger<IndexBuilder> _logger;
    private int _building;

    /// <summary>Initializes a new instance of the <see cref="IndexBuilder"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="documents">Document builder.</param>
    /// <param name="embeddings">Embeddings client.</param>
    /// <param name="index">The in-memory index to swap on success.</param>
    /// <param name="store">The on-disk persistence.</param>
    /// <param name="scope">Leaves out libraries excluded from AI Search.</param>
    /// <param name="logger">Logger.</param>
    public IndexBuilder(
        ILibraryManager libraryManager,
        DocumentBuilder documents,
        EmbeddingsClient embeddings,
        VectorIndex index,
        VectorIndexStore store,
        LibraryScope scope,
        ILogger<IndexBuilder> logger)
    {
        _libraryManager = libraryManager;
        _documents = documents;
        _embeddings = embeddings;
        _index = index;
        _store = store;
        _scope = scope;
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
    /// <param name="progress">Optional progress sink (0-100), used by the scheduled task.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The build outcome.</returns>
    public async Task<IndexBuildResult?> BuildAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var target = EmbeddingsTarget.FromConfiguration(config);
        if (target is null)
        {
            _logger.LogInformation("AiSearch: semantic index build skipped, no embedding model/endpoint configured.");
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
        items = _scope.Filter(Plugin.Instance!.Configuration, items).ToList();

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
        var embedded = 0;
        var skipped = new List<string>();
        EmbeddingsException? firstError = null;
        for (var offset = 0; offset < pending.Count; offset += EmbedBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.GetRange(offset, Math.Min(EmbedBatchSize, pending.Count - offset));
            List<float[]?> vectors;
            try
            {
                vectors = (await _embeddings
                    .EmbedAsync(target, batch.ConvertAll(entry => entry.Text), cancellationToken)
                    .ConfigureAwait(false)).ConvertAll(v => (float[]?)v);
            }
            catch (EmbeddingsException ex)
            {
                // One bad title (NaN on some GPU backends) must not sink the whole
                // build: embed this batch one title at a time and skip the ones
                // that still fail. If nothing at all works, report why.
                firstError ??= ex;
                vectors = new List<float[]?>(batch.Count);
                foreach (var entry in batch)
                {
                    try
                    {
                        vectors.Add((await _embeddings.EmbedAsync(target, new[] { entry.Text }, cancellationToken).ConfigureAwait(false))[0]);
                    }
                    catch (EmbeddingsException)
                    {
                        vectors.Add(null);
                    }
                }
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var vector = vectors[i];
                if (vector is null || (dimensions > 0 && vector.Length != dimensions))
                {
                    skipped.Add(batch[i].Item.Name);
                    continue;
                }

                VectorMath.NormalizeInPlace(vector);
                dimensions = vector.Length;
                entries.Add(new IndexEntry(batch[i].Item.Id, batch[i].Hash, vector));
                embedded++;
            }

            // Every title of the first batches failing means the endpoint, not
            // the titles, is the problem: stop early with its explanation.
            if (embedded == 0 && skipped.Count >= Math.Min(pending.Count, EmbedBatchSize * 2) && firstError is not null)
            {
                throw firstError;
            }

            progress?.Report(100.0 * Math.Min(offset + batch.Count, pending.Count) / Math.Max(1, pending.Count));
        }

        if (skipped.Count > 0)
        {
            _logger.LogWarning(
                "AiSearch: {Count} titles could not be embedded and were left out of the index ({Error}). First ones: {Titles}",
                skipped.Count,
                firstError?.Message,
                string.Join(", ", skipped.Take(10)));
        }

        var snapshot = new IndexSnapshot(target.Model, dimensions, DateTime.UtcNow, entries);
        _index.Replace(snapshot);
        _store.Save(snapshot);
        _logger.LogInformation(
            "AiSearch: semantic index built, {Embedded} embedded, {Reused} reused, {Skipped} skipped, {Total} total ({Model}).",
            embedded,
            entries.Count - embedded,
            skipped.Count,
            entries.Count,
            target.Model);
        progress?.Report(100);
        var warning = skipped.Count > 0 ? $"{skipped.Count} titles were skipped: {firstError?.Message}" : null;
        return new IndexBuildResult(startedAt, DateTime.UtcNow, embedded, entries.Count - embedded, entries.Count, warning);
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
