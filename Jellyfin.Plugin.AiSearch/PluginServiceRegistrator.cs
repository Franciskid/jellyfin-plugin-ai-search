using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using Jellyfin.Plugin.AiSearch.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AiSearch;

/// <summary>
/// Registers plugin services with the host DI container: the web-client
/// injection, the semantic index (in-memory + on-disk + builder), and the
/// outbound clients the controllers use.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<WebInjectionHostedService>();

        // Semantic index: one shared in-memory index, persisted to disk, built
        // by the scheduled task / config-page button, loaded back at startup.
        serviceCollection.AddSingleton<VectorIndex>();
        serviceCollection.AddSingleton<VectorIndexStore>();
        serviceCollection.AddSingleton<DocumentBuilder>();
        serviceCollection.AddSingleton<EmbeddingsClient>();
        serviceCollection.AddSingleton<IndexBuilder>();
        serviceCollection.AddSingleton<SemanticRetriever>();
        serviceCollection.AddHostedService<IndexStartupService>();

        // Direct-mode chat caller.
        serviceCollection.AddSingleton<DirectChatClient>();

        // Per-user search history + taste profile (JSON files under the data folder).
        serviceCollection.AddSingleton<HistoryStore>();
        serviceCollection.AddSingleton<TasteProfileStore>();
    }
}
