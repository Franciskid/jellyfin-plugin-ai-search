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
/// Minimal client for the OpenAI-compatible <c>/v1/embeddings</c> endpoint
/// (OpenAI, Ollama, LiteLLM, …). Stateless: the target is passed per call so
/// configuration changes apply immediately.
/// </summary>
public class EmbeddingsClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="EmbeddingsClient"/> class.</summary>
    /// <param name="httpClientFactory">Factory for outbound HTTP clients.</param>
    public EmbeddingsClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Embeds a batch of strings, preserving input order. Vectors are returned
    /// raw (not normalized) — callers normalize once, where it matters.
    /// </summary>
    /// <param name="target">Endpoint, key and model to use.</param>
    /// <param name="inputs">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input, in input order.</returns>
    /// <exception cref="EmbeddingsException">When the endpoint errors or returns an unexpected shape.</exception>
    public async Task<List<float[]>> EmbedAsync(EmbeddingsTarget target, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return new List<float[]>();
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(target.TimeoutSeconds);
        if (!string.IsNullOrEmpty(target.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
        }

        var url = target.BaseUrl + "/v1/embeddings";
        using var response = await client
            .PostAsJsonAsync(url, new { model = target.Model, input = inputs }, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new EmbeddingsException($"Embeddings request failed ({(int)response.StatusCode}): {Truncate(body, 300)}");
        }

        return Parse(body, inputs.Count);
    }

    private static List<float[]> Parse(string body, int expected)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new EmbeddingsException("Embeddings response has no data array.");
        }

        // Order by the returned index when present; otherwise keep response order.
        var rows = new List<(int Index, float[] Vector)>(expected);
        var position = 0;
        foreach (var row in data.EnumerateArray())
        {
            if (!row.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
            {
                throw new EmbeddingsException("Embeddings response row has no embedding.");
            }

            var vector = new float[embedding.GetArrayLength()];
            var i = 0;
            foreach (var value in embedding.EnumerateArray())
            {
                vector[i++] = value.GetSingle();
            }

            var index = row.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                ? indexElement.GetInt32()
                : position;
            rows.Add((index, vector));
            position++;
        }

        if (rows.Count != expected)
        {
            throw new EmbeddingsException($"Embeddings count mismatch: got {rows.Count}, expected {expected}.");
        }

        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows.ConvertAll(row => row.Vector);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Raised when the embeddings endpoint fails or misbehaves.</summary>
public class EmbeddingsException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="EmbeddingsException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public EmbeddingsException(string message)
        : base(message)
    {
    }
}
