using System;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.SearchLog;

/// <summary>
/// The queue side of the search log: request-handling code (the native-search
/// filter, the AI popup controller) calls <see cref="Enqueue"/> and returns
/// immediately; <see cref="SearchLogWriter"/> drains the queue on a background
/// thread and does the actual file I/O, so logging never adds request latency.
/// </summary>
public sealed class SearchLogStore
{
    private const int Capacity = 10_000;
    private static readonly TimeSpan WarnInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<SearchLogEntry> _channel;
    private readonly ILogger<SearchLogStore> _logger;
    private long _lastDropWarnTicks;

    /// <summary>Initializes a new instance of the <see cref="SearchLogStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public SearchLogStore(ILogger<SearchLogStore> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<SearchLogEntry>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>The channel <see cref="SearchLogWriter"/> reads from.</summary>
    internal ChannelReader<SearchLogEntry> Reader => _channel.Reader;

    /// <summary>
    /// Queues an entry for the background writer. Never blocks; when the queue
    /// is already at its 10,000-entry cap, the entry is dropped and a warning is
    /// logged, rate-limited to once a minute so a sustained backlog cannot spam
    /// the log itself.
    /// </summary>
    /// <param name="entry">The entry to write. <see cref="SearchLogEntry.Id"/> is assigned later, by the writer.</param>
    public void Enqueue(SearchLogEntry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            return;
        }

        WarnDropped();
    }

    private void WarnDropped()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDropWarnTicks);
        if (now - last < WarnInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDropWarnTicks, now, last) == last)
        {
            _logger.LogWarning("AiSearch: search log queue is full (>{Capacity}); dropping new entries until it drains.", Capacity);
        }
    }
}
