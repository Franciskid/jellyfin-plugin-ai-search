using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Search;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiSearch.SearchLog;

/// <summary>
/// Registered globally (<c>MvcOptions.Filters.AddService&lt;SearchLogFilter&gt;()</c>),
/// so it sees every controller action in the server, not just this plugin's
/// own. Captures native Jellyfin searches (<c>/Items</c>, <c>/Search/Hints</c>,
/// anything called with a non-empty <c>searchTerm</c>) into the search log when
/// <see cref="Configuration.PluginConfiguration.LogSearches"/> is on. A cheap
/// early-out skips everything else, so this adds no measurable overhead to the
/// vast majority of requests that are not searches.
/// </summary>
public sealed class SearchLogFilter : IAsyncActionFilter
{
    private readonly SearchLogStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SearchLogFilter> _logger;

    /// <summary>Initializes a new instance of the <see cref="SearchLogFilter"/> class.</summary>
    /// <param name="store">The search log queue.</param>
    /// <param name="userManager">User manager, to resolve the caller's user name.</param>
    /// <param name="libraryManager">Library manager, to resolve the <c>parentId</c> parameter's name.</param>
    /// <param name="logger">Logger.</param>
    public SearchLogFilter(SearchLogStore store, IUserManager userManager, ILibraryManager libraryManager, ILogger<SearchLogFilter> logger)
    {
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Decide whether to log, guarded: a bad config read or query-parsing
        // failure here must not stop the request. next() itself is called
        // exactly once below, and never from inside a try/catch, so a real
        // exception from the action always propagates normally.
        var shouldLog = false;
        var searchTerm = string.Empty;
        try
        {
            shouldLog = Plugin.Instance!.Configuration.LogSearches
                && TryGetSearchTerm(context.HttpContext.Request.Query, out searchTerm);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AiSearch: search log capture failed.");
        }

        if (!shouldLog)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var executed = await next().ConfigureAwait(false);
        stopwatch.Stop();

        try
        {
            _store.Enqueue(BuildEntry(context.HttpContext, startedAt, stopwatch.ElapsedMilliseconds, searchTerm, executed));
        }
        catch (Exception ex)
        {
            // Logging must never break a real request that already succeeded:
            // if building or queuing the entry goes wrong, drop it.
            _logger.LogWarning(ex, "AiSearch: search log capture failed.");
        }
    }

    private static bool TryGetSearchTerm(IQueryCollection query, out string searchTerm)
    {
        foreach (var pair in query)
        {
            if (!string.Equals(pair.Key, "searchTerm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pair.Value.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                break;
            }

            searchTerm = value.Trim();
            return true;
        }

        searchTerm = string.Empty;
        return false;
    }

    private SearchLogEntry BuildEntry(HttpContext context, DateTime startedAt, long durationMs, string searchTerm, ActionExecutedContext executed)
    {
        var entry = new SearchLogEntry
        {
            Timestamp = startedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Kind = "search",
            Endpoint = context.Request.Path.Value ?? string.Empty,
            Method = context.Request.Method,
            DurationMs = durationMs,
            SearchTerm = searchTerm
        };

        SearchLogEntryBuilder.ApplyRequest(entry, context, _userManager, _libraryManager);

        var trace = context.Items.TryGetValue(SemanticSearchTrace.HttpContextKey, out var traceObj) ? traceObj as SemanticSearchTrace : null;
        entry.Semantic = trace is null
            ? null
            : new SearchLogSemanticSummary
            {
                Enabled = trace.Enabled,
                Ran = trace.Ran,
                SkippedReason = trace.SkippedReason,
                Mode = trace.Mode,
                DurationMs = trace.DurationMs,
                RawHits = trace.RawHits,
                Matches = trace.Matches,
                Error = trace.Error
            };

        entry.StatusCode = (executed.Result as ObjectResult)?.StatusCode
            ?? (executed.Result as StatusCodeResult)?.StatusCode
            ?? (executed.Exception is not null && !executed.ExceptionHandled ? StatusCodes.Status500InternalServerError : StatusCodes.Status200OK);

        if (executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> queryResult })
        {
            entry.ResultCount = queryResult.Items.Count;
            entry.TotalRecordCount = queryResult.TotalRecordCount;
            entry.Results = SearchLogEntryBuilder.FromItems(queryResult.Items, trace);
            entry.ResultsLogged = entry.Results.Count;
        }
        else if (executed.Result is ObjectResult { Value: SearchHintResult hintResult })
        {
            entry.ResultCount = hintResult.SearchHints.Count;
            entry.TotalRecordCount = hintResult.TotalRecordCount;
            entry.Results = SearchLogEntryBuilder.FromHints(hintResult.SearchHints, trace);
            entry.ResultsLogged = entry.Results.Count;
        }

        return entry;
    }
}
