using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AiSearch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AiSearch;

/// <summary>
/// AI Search &amp; Recommendation plugin. Thin server-side plugin that forwards a
/// natural-language prompt (plus the user's own library + taste signals) to an
/// AI backend and renders library recommendations in the web client.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "AI Search";

    /// <inheritdoc />
    public override string Description =>
        "AI natural-language search & recommendations from your own movie library.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d0a3f1e6-9c2b-4a7d-8e5f-1b2c3d4e5f6a");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "AiSearch",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
        }
    };
}
