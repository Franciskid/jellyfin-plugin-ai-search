using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiSearch.Search;

/// <summary>
/// Outcome of one <see cref="RemoteSearchClient.SearchAsync"/> call: either the
/// ranked candidates, or an error describing why there are none. Never both.
/// </summary>
/// <param name="Results">Ranked (item id, score) pairs, best first, or <c>null</c> on failure.</param>
/// <param name="Error">A short description of the failure, or <c>null</c> on success.</param>
public sealed record RemoteSearchResponse(List<(Guid ItemId, float Score)>? Results, string? Error);

/// <summary>
/// Calls a remote search endpoint and parses its ranked-candidate response.
/// Shared by the AI popup controller and the native-search provider: this
/// class only does the HTTP call and response parsing, response order is
/// preserved as rank order. It never logs and never decides what a failure
/// means, each caller does: the controller logs a warning and falls back to a
/// catalog slice, the native-search provider records the error in its
/// per-request trace and rate-limits its own warning.
/// </summary>
public sealed class RemoteSearchClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="RemoteSearchClient"/> class.</summary>
    /// <param name="httpClientFactory">Factory for outbound HTTP clients.</param>
    public RemoteSearchClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// POSTs <paramref name="payload"/> to a remote search endpoint and returns
    /// its ranked candidates (best first, response order preserved), or an
    /// error describing why there are none: a non-success status, a network
    /// error, or a response that is not <c>{"results":[{"itemId":...},...]}</c>.
    /// Nothing is retried. When <paramref name="cancellationToken"/> is the one
    /// that gets cancelled (caller cancellation, or a caller-imposed timeout
    /// such as the native-search provider's 5-second window), the resulting
    /// <see cref="OperationCanceledException"/> propagates instead of becoming
    /// an error result, so callers can tell a real timeout/cancellation apart
    /// from an ordinary failure.
    /// </summary>
    /// <param name="endpointUrl">The full search endpoint URL.</param>
    /// <param name="apiKey">Bearer key for the endpoint (blank if it needs none).</param>
    /// <param name="timeoutSeconds">Request timeout, clamped to 5-120 seconds.</param>
    /// <param name="payload">The request body, serialized as JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ranked candidates, or an error.</returns>
    public async Task<RemoteSearchResponse> SearchAsync(
        string endpointUrl,
        string apiKey,
        int timeoutSeconds,
        object payload,
        CancellationToken cancellationToken)
    {
        string raw;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await client.PostAsJsonAsync(endpointUrl.Trim(), payload, cancellationToken).ConfigureAwait(false);
            raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new RemoteSearchResponse(null, $"{(int)resp.StatusCode} {resp.StatusCode}: {Trunc(raw, 300)}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RemoteSearchResponse(null, ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return new RemoteSearchResponse(null, $"unexpected response shape: {Trunc(raw, 300)}");
            }

            var ranked = new List<(Guid ItemId, float Score)>(results.GetArrayLength());
            foreach (var r in results.EnumerateArray())
            {
                if (Guid.TryParse(GetString(r, "itemId"), out var g))
                {
                    var score = r.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetSingle() : 0f;
                    ranked.Add((g, score));
                }
            }

            return new RemoteSearchResponse(ranked, null);
        }
        catch (JsonException ex)
        {
            return new RemoteSearchResponse(null, $"unexpected response: {Trunc(raw, 300)} ({ex.Message})");
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}
