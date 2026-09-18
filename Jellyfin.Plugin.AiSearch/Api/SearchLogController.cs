using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>
/// Read side of the search log (<see cref="SearchLog.SearchLogWriter"/> is the
/// write side), for an external collector or another admin tool to page
/// through. Admin only, reachable regardless of
/// <see cref="Configuration.PluginConfiguration.NativeSearchOnly"/>.
/// </summary>
[ApiController]
[Route("AiSearch")]
[Authorize(AuthenticationSchemes = "CustomAuthentication")]
public class SearchLogController : ControllerBase
{
    private readonly string _dir;

    /// <summary>Initializes a new instance of the <see cref="SearchLogController"/> class.</summary>
    /// <param name="paths">Jellyfin application paths (for the data folder).</param>
    public SearchLogController(IApplicationPaths paths)
    {
        _dir = Path.Combine(paths.DataPath, "aisearch", "searchlog");
    }

    /// <summary>
    /// Returns logged searches, oldest first within the page, id-paginated via
    /// <paramref name="afterId"/>.
    /// </summary>
    /// <param name="from">Inclusive lower timestamp bound (UTC), or unbounded when omitted.</param>
    /// <param name="to">Exclusive upper timestamp bound (UTC), or unbounded when omitted.</param>
    /// <param name="afterId">Only return entries with an id greater than this.</param>
    /// <param name="limit">Maximum entries to return, clamped to 1-5000 (default 1000).</param>
    /// <returns>A page of raw log entries plus paging and logging-state metadata.</returns>
    [HttpGet("SearchLog")]
    public ActionResult SearchLog([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] long? afterId, [FromQuery] int limit = 1000)
    {
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        Response.Headers["Cache-Control"] = "no-store";

        var config = Plugin.Instance!.Configuration;
        var cappedLimit = Math.Clamp(limit, 1, 5000);
        var fromUtc = from?.ToUniversalTime();
        var toUtc = to?.ToUniversalTime();

        var files = ListLogFiles();
        var oldestTimestamp = FindOldestTimestamp(files);

        var matches = new List<(long Id, JsonNode Node)>();
        foreach (var (date, path) in files)
        {
            // Only read files whose [date, date+1day) range overlaps [from, to).
            var fileStart = date;
            var fileEnd = date.AddDays(1);
            if (fromUtc is DateTime f && fileEnd <= f)
            {
                continue;
            }

            if (toUtc is DateTime t && fileStart >= t)
            {
                continue;
            }

            foreach (var line in SafeReadLines(path))
            {
                if (!TryParseLine(line, out var id, out var timestamp, out var node))
                {
                    continue;
                }

                if (afterId is long after && id <= after)
                {
                    continue;
                }

                if (fromUtc is DateTime fb && timestamp < fb)
                {
                    continue;
                }

                if (toUtc is DateTime tb && timestamp >= tb)
                {
                    continue;
                }

                matches.Add((id, node));
            }
        }

        matches.Sort((a, b) => a.Id.CompareTo(b.Id));

        var hasMore = matches.Count > cappedLimit;
        var page = hasMore ? matches.Take(cappedLimit).ToList() : matches;

        return Ok(new
        {
            entries = page.Select(m => m.Node),
            hasMore,
            nextAfterId = page.Count > 0 ? page[^1].Id : (long?)null,
            loggingEnabled = config.LogSearches,
            retentionDays = Math.Clamp(config.SearchLogRetentionDays, 1, 365),
            oldestTimestamp = oldestTimestamp?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
        });
    }

    // The oldest entry the plugin still holds: the first parseable line of the
    // oldest non-empty file, not just that file's date, so a collector knows
    // exactly how far back the retained log actually reaches.
    private static DateTime? FindOldestTimestamp(List<(DateTime Date, string Path)> files)
    {
        foreach (var (_, path) in files)
        {
            foreach (var line in SafeReadLines(path))
            {
                if (TryParseLine(line, out _, out var timestamp, out _))
                {
                    return timestamp;
                }
            }
        }

        return null;
    }

    private List<(DateTime Date, string Path)> ListLogFiles()
    {
        if (!Directory.Exists(_dir))
        {
            return new List<(DateTime, string)>();
        }

        var files = new List<(DateTime Date, string Path)>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(
                    name,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var date))
            {
                files.Add((date, file));
            }
        }

        files.Sort((a, b) => a.Date.CompareTo(b.Date));
        return files;
    }

    private static bool TryParseLine(string line, out long id, out DateTime timestamp, out JsonNode node)
    {
        id = 0;
        timestamp = default;
        node = null!;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject obj
            || obj["id"] is not JsonValue idValue
            || !idValue.TryGetValue(out long parsedId)
            || obj["timestamp"] is not JsonValue tsValue
            || !tsValue.TryGetValue(out string? tsText)
            || !DateTime.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedTs))
        {
            return false;
        }

        id = parsedId;
        timestamp = parsedTs;
        node = parsed;
        return true;
    }

    private static List<string> SafeReadLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (IOException)
        {
            // The writer may be mid-append, or pruning may have deleted the file
            // between listing and reading; either way, skip it for this page.
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
