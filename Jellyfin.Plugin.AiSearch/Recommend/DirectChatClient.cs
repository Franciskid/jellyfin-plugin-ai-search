using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiSearch.Configuration;

namespace Jellyfin.Plugin.AiSearch.Recommend;

/// <summary>
/// Calls an OpenAI-compatible <c>/v1/chat/completions</c> endpoint and returns
/// the assistant's content. Asks for JSON output first and quietly retries
/// without <c>response_format</c> for servers that reject it (some local
/// runtimes and older proxies do).
/// </summary>
public class DirectChatClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="DirectChatClient"/> class.</summary>
    /// <param name="httpClientFactory">Factory for outbound HTTP clients.</param>
    public DirectChatClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Runs one completion and returns the assistant message content.</summary>
    /// <param name="config">Plugin configuration (endpoint, key, model, timeout).</param>
    /// <param name="messages">The chat messages payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant content string.</returns>
    /// <exception cref="DirectChatException">When the endpoint errors or replies with an unexpected shape.</exception>
    public async Task<string> CompleteAsync(PluginConfiguration config, object[] messages, CancellationToken cancellationToken)
    {
        var url = config.DirectEndpointUrl.TrimEnd('/') + "/v1/chat/completions";

        var (ok, status, body) = await PostAsync(config, url, messages, wantJson: true, cancellationToken).ConfigureAwait(false);
        if (!ok && status == 400)
        {
            (ok, status, body) = await PostAsync(config, url, messages, wantJson: false, cancellationToken).ConfigureAwait(false);
        }

        if (!ok)
        {
            throw new DirectChatException($"Direct endpoint returned {status}: {Truncate(body, 300)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new DirectChatException($"Unexpected direct response: {Truncate(body, 300)}");
        }
    }

    private async Task<(bool Ok, int Status, string Body)> PostAsync(
        PluginConfiguration config,
        string url,
        object[] messages,
        bool wantJson,
        CancellationToken cancellationToken)
    {
        object payload = wantJson
            ? new { model = config.Model, temperature = 0.4, max_tokens = 900, response_format = new { type = "json_object" }, messages }
            : new { model = config.Model, temperature = 0.4, max_tokens = 900, messages };

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120));
        if (!string.IsNullOrEmpty(config.DirectApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DirectApiKey);
        }

        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Raised when the direct chat endpoint fails or misbehaves.</summary>
public class DirectChatException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DirectChatException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public DirectChatException(string message)
        : base(message)
    {
    }
}
