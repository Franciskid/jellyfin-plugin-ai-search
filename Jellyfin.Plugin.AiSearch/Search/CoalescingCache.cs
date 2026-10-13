using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// A small LRU cache of completed results plus the calls still in flight, keyed
/// by string. Concurrent callers asking for the same key share one call, and
/// the call itself never runs on a caller's token: when a caller gives up (it
/// cancelled, or its time budget ran out) the call keeps going in the
/// background so its result lands in the cache for the next keystroke, and a
/// cold model gets loaded instead of being abandoned halfway. Failures are not
/// cached.
/// </summary>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class CoalescingCache<TValue>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _done = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, Task<TValue>> _inFlight = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    /// <summary>Initializes a new instance of the <see cref="CoalescingCache{TValue}"/> class.</summary>
    /// <param name="capacity">How many completed results to keep.</param>
    /// <param name="ttl">How long a completed result stays valid.</param>
    public CoalescingCache(int capacity, TimeSpan ttl)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl;
    }

    /// <summary>Returns a cached value without starting anything.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The cached value, when found.</param>
    /// <returns><c>true</c> on a fresh hit.</returns>
    public bool TryGet(string key, out TValue value)
    {
        lock (_gate)
        {
            if (_done.TryGetValue(key, out var node))
            {
                if (DateTime.UtcNow - node.Value.At < _ttl)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }

                _lru.Remove(node);
                _done.Remove(key);
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Returns the cached value, joins the call already running for this key,
    /// or starts <paramref name="factory"/>; then waits for it on the caller's
    /// <paramref name="cancellationToken"/> only.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Starts the underlying call, on its own token.</param>
    /// <param name="timeout">Hard limit for the underlying call itself.</param>
    /// <param name="cancellationToken">The caller's token; cancelling it stops the wait, not the call.</param>
    /// <returns>The value and whether it came from the cache.</returns>
    public async Task<(TValue Value, bool Cached)> GetAsync(
        string key,
        Func<CancellationToken, Task<TValue>> factory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (TryGet(key, out var hit))
        {
            return (hit, true);
        }

        Task<TValue> task;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(key, out task!))
            {
                task = Start(key, factory, timeout);
            }
        }

        var value = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (value, false);
    }

    /// <summary>
    /// Starts the call for <paramref name="key"/> in the background, unless it
    /// is cached or already running. Used to warm a cold backend.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Starts the underlying call, on its own token.</param>
    /// <param name="timeout">Hard limit for the underlying call itself.</param>
    /// <returns>The running call, or a completed task when nothing was started.</returns>
    public Task Prefetch(string key, Func<CancellationToken, Task<TValue>> factory, TimeSpan timeout)
    {
        if (TryGet(key, out _))
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var running))
            {
                return running;
            }

            return Start(key, factory, timeout);
        }
    }

    // Called under the lock. The task cannot complete before the caller
    // releases the lock (its cleanup takes the same lock), so registering it
    // here never races with its own removal.
    private Task<TValue> Start(string key, Func<CancellationToken, Task<TValue>> factory, TimeSpan timeout)
    {
        var task = RunAsync(key, factory, timeout);
        _inFlight[key] = task;

        // Nobody may be waiting any more when a background call fails; observe
        // the exception so it is not reported as unobserved.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task<TValue> RunAsync(string key, Func<CancellationToken, Task<TValue>> factory, TimeSpan timeout)
    {
        // Yield first so the factory never runs under the caller's lock.
        await Task.Yield();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            var value = await factory(timeoutSource.Token).ConfigureAwait(false);
            Store(key, value);
            return value;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private void Store(string key, TValue value)
    {
        lock (_gate)
        {
            if (_done.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
            }

            var node = _lru.AddFirst(new Entry(key, value, DateTime.UtcNow));
            _done[key] = node;
            while (_done.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _done.Remove(last.Value.Key);
            }
        }
    }

    private sealed record Entry(string Key, TValue Value, DateTime At);
}
