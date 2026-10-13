using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Registered globally, like the search log filter. After a native search
/// action ran, tells the client which results only the plugin's meaning match
/// found, so the web client can label them apart from Jellyfin's own title
/// matches. The ids go in an <c>X-AiSearch-Matches</c> response header (the
/// response body is Jellyfin's own DTO and stays untouched), and the
/// retrieval time in a standard <c>Server-Timing</c> entry. Requests the
/// semantic provider never looked at carry no trace and are passed through.
/// </summary>
public sealed class SearchOriginFilter : IAsyncActionFilter
{
    /// <summary>Response header listing the semantic-only result ids (32-hex, comma-separated).</summary>
    public const string MatchesHeader = "X-AiSearch-Matches";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await next().ConfigureAwait(false);

        try
        {
            var http = context.HttpContext;
            if (!http.Items.TryGetValue(SemanticSearchTrace.HttpContextKey, out var value)
                || value is not SemanticSearchTrace { Ran: true } trace
                || http.Response.HasStarted)
            {
                return;
            }

            var headers = http.Response.Headers;
            var description = trace.Mode + (trace.Cached ? ", cached" : string.Empty) + (trace.Error is null ? string.Empty : ", " + trace.Error);
            headers.Append("Server-Timing", string.Create(
                CultureInfo.InvariantCulture,
                $"aisearch;dur={trace.DurationMs};desc=\"AI Search ({description})\""));

            var semanticOnly = trace.Hits
                .Where(pair => !pair.Value.TitleMatch)
                .OrderBy(pair => pair.Value.Rank)
                .Select(pair => pair.Key.ToString("N"))
                .ToArray();
            headers[MatchesHeader] = string.Join(',', semanticOnly);
            headers.Append("Access-Control-Expose-Headers", MatchesHeader + ", Server-Timing");
        }
        catch (Exception)
        {
            // A header is a nicety; never fail a search over it.
        }
    }
}
