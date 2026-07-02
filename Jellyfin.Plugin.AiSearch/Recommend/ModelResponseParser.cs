using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>One pick from the model: an index into the candidate list plus a one-line reason.</summary>
public sealed class ModelRecommendation
{
    /// <summary>Gets or sets the candidate index.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the model's one-line justification.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>The parsed model reply: a one-sentence answer and the picks.</summary>
public sealed class ParsedRecommendations
{
    /// <summary>Gets or sets the one-sentence summary answer.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>Gets the recommendations, in model order.</summary>
    public List<ModelRecommendation> Recommendations { get; } = new();
}

/// <summary>
/// Tolerant extraction of the strict-JSON contract from a model reply: strips
/// code fences, slices to the outermost object, accepts string indices —
/// models drift, users shouldn't see 502s for a stray backtick.
/// </summary>
public static class ModelResponseParser
{
    /// <summary>Parses the model content, or returns <c>null</c> when unusable.</summary>
    /// <param name="content">The raw assistant message content.</param>
    /// <returns>The parsed result, or <c>null</c>.</returns>
    public static ParsedRecommendations? Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = StripFences(content.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            var root = doc.RootElement;
            var result = new ParsedRecommendations
            {
                Answer = GetString(root, "answer") ?? string.Empty,
            };
            if (root.TryGetProperty("recommendations", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var rec in recs.EnumerateArray())
                {
                    var index = ReadIndex(rec);
                    if (index >= 0)
                    {
                        result.Recommendations.Add(new ModelRecommendation
                        {
                            Index = index,
                            Reason = GetString(rec, "reason") ?? string.Empty,
                        });
                    }
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var newline = text.IndexOf('\n');
        if (newline >= 0)
        {
            text = text[(newline + 1)..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text.Trim();
    }

    private static int ReadIndex(JsonElement rec)
    {
        if (!rec.TryGetProperty("index", out var element))
        {
            return -1;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        return -1;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
