using Jellyfin.Plugin.AiSearch.History;
using Jellyfin.Plugin.AiSearch.Recommend;
using Jellyfin.Plugin.AiSearch.Search;
using Jellyfin.Plugin.AiSearch.SearchLog;
using Jellyfin.Plugin.AiSearch.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
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
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlScriptStartupFilter>();

        // Semantic index: one shared in-memory index, persisted to disk, built
        // by the scheduled task / config-page button, loaded back at startup.
        serviceCollection.AddSingleton<VectorIndex>();
        serviceCollection.AddSingleton<VectorIndexStore>();
        serviceCollection.AddSingleton<DocumentBuilder>();
        serviceCollection.AddSingleton<EmbeddingsClient>();
        serviceCollection.AddSingleton<IndexBuilder>();
        serviceCollection.AddSingleton<SemanticRetriever>();
        serviceCollection.AddSingleton<SemanticWarmer>();
        serviceCollection.AddSingleton<SemanticMatcher>();
        serviceCollection.AddSingleton<LibraryScope>();
        serviceCollection.AddHostedService<IndexStartupService>();

        // Tells the web client which native search results came from the
        // plugin's meaning match rather than Jellyfin's title search.
        serviceCollection.AddSingleton<SearchOriginFilter>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.AddService<SearchOriginFilter>());

        // Shared remote search endpoint caller: the AI popup controller and the
        // native-search provider (SemanticSearchProvider) both use it.
        serviceCollection.AddSingleton<RemoteSearchClient>();

        // Direct-mode chat caller.
        serviceCollection.AddSingleton<DirectChatClient>();

        // Per-user search history + taste profile (JSON files under the data folder).
        serviceCollection.AddSingleton<HistoryStore>();
        serviceCollection.AddSingleton<TasteProfileStore>();

        // Search log: a global MVC filter captures every native Jellyfin search
        // (not just this plugin's own controllers), a queue decouples that from
        // request latency, and a background writer does the file I/O.
        serviceCollection.AddSingleton<SearchLogStore>();
        serviceCollection.AddSingleton<SearchLogFilter>();
        serviceCollection.Configure<MvcOptions>(o => o.Filters.AddService<SearchLogFilter>());
        serviceCollection.AddHostedService<SearchLogWriter>();
    }
}
