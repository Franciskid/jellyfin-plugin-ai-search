using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Loads the persisted semantic index into memory when the server starts, so
/// search works immediately after a restart without re-embedding anything.
/// Building (first time or after changes) is the scheduled task's job.
/// </summary>
public class IndexStartupService : IHostedService
{
    private readonly VectorIndexStore _store;
    private readonly VectorIndex _index;

    /// <summary>Initializes a new instance of the <see cref="IndexStartupService"/> class.</summary>
    /// <param name="store">The on-disk index store.</param>
    /// <param name="index">The in-memory index.</param>
    public IndexStartupService(VectorIndexStore store, VectorIndex index)
    {
        _store = store;
        _index = index;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Load off the startup path — a large index shouldn't delay the server.
        _ = Task.Run(
            () =>
            {
                var snapshot = _store.Load();
                if (snapshot is not null)
                {
                    _index.Replace(snapshot);
                }
            },
            cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
