using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.SearchLog;

/// <summary>
/// The single writer for the search log: drains <see cref="SearchLogStore"/> on
/// a background thread, assigns each entry's strictly increasing id, and
/// appends one compact JSON line per entry to
/// <c>{DataPath}/aisearch/searchlog/{yyyy-MM-dd}.jsonl</c> (UTC date of the
/// entry's own timestamp). Also seeds the id counter from the newest existing
/// file at startup, and prunes files older than the configured retention at
/// startup and every hour after.
/// </summary>
public sealed class SearchLogWriter : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly SearchLogStore _store;
    private readonly string _dir;
    private readonly ILogger<SearchLogWriter> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Timer? _pruneTimer;
    private long _lastId;

    /// <summary>Initializes a new instance of the <see cref="SearchLogWriter"/> class.</summary>
    /// <param name="store">The queue to drain.</param>
    /// <param name="paths">Jellyfin application paths (for the data folder).</param>
    /// <param name="logger">Logger.</param>
    public SearchLogWriter(SearchLogStore store, IApplicationPaths paths, ILogger<SearchLogWriter> logger)
    {
        _store = store;
        _dir = Path.Combine(paths.DataPath, "aisearch", "searchlog");
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _pruneTimer = new Timer(_ => Prune(), null, PruneInterval, PruneInterval);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _pruneTimer?.Dispose();
        _cts?.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the read loop stops as soon as its own token (just
                // cancelled above) is observed.
            }
        }

        // The queue can still hold entries enqueued right before shutdown, e.g.
        // a search that finished a moment before the process stopped. Drain and
        // write them now, bounded by the host's own stop token so this cannot
        // hang shutdown indefinitely.
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host forced an immediate stop mid-drain; whatever is left
            // queued is lost, same as before this entries-drain existed.
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _store.Reader.TryRead(out var entry))
        {
            await WriteOneAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        SeedLastId();
        Prune();

        try
        {
            await foreach (var entry in _store.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteOneAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    // Assigns the next strictly increasing id and writes one entry, used by
    // both the normal read loop and the shutdown drain. A write failure for
    // one entry is logged and skipped, it never stops the loop it's called from.
    private async Task WriteOneAsync(SearchLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            entry.Id = Math.Max(_lastId + 1, DateTime.UtcNow.Ticks);
            _lastId = entry.Id;
            await WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AiSearch: could not write a search log entry.");
        }
    }

    private async Task WriteEntryAsync(SearchLogEntry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dir);
        var date = DateTime.TryParse(
            entry.Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;
        var path = Path.Combine(_dir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        var json = JsonSerializer.Serialize(entry, JsonOptions);

        // FileShare.ReadWrite so a concurrent GET /AiSearch/SearchLog read never
        // hits a sharing violation while this append is in flight.
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private void SeedLastId()
    {
        try
        {
            var newest = NewestLogDate();
            if (newest is null)
            {
                return;
            }

            var path = Path.Combine(_dir, newest + ".jsonl");
            if (!File.Exists(path))
            {
                return;
            }

            var lines = File.ReadAllLines(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (TryGetId(lines[i], out var id))
                {
                    _lastId = id;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not seed the search log id counter; starting from 0.");
        }
    }

    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_dir))
            {
                return;
            }

            var retentionDays = Math.Clamp(Plugin.Instance!.Configuration.SearchLogRetentionDays, 1, 365);
            var cutoff = DateTime.UtcNow.Date.AddDays(-retentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate)
                    && fileDate < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: could not prune old search log files.");
        }
    }

    private string? NewestLogDate()
    {
        if (!Directory.Exists(_dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(_dir, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool TryGetId(string line, out long id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
            {
                id = idProp.GetInt64();
                return true;
            }
        }
        catch (JsonException)
        {
            // Half-written or corrupt line; skip it and try the previous one.
        }

        return false;
    }
}
