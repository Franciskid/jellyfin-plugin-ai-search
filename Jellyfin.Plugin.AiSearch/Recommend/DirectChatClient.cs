using System;
using System.Collections.Concurrent;
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
/// the assistant's content. Servers disagree on a few request fields, so a
/// rejected request is retried with the field the error points at changed:
/// <c>max_tokens</c> becomes <c>max_completion_tokens</c> (newer OpenAI
/// models refuse the old name), <c>temperature</c> is dropped (reasoning
/// models only accept the default), and <c>response_format</c> is dropped
/// (some local runtimes and proxies reject it). What worked is remembered per
/// endpoint and model, so only the first call pays for the retries.
/// </summary>
public class DirectChatClient
{
    private const int TokenLimit = 4096;
    private const int MaxAttempts = 4;

    private static readonly ConcurrentDictionary<string, RequestShape> Learned = new(StringComparer.Ordinal);

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="DirectChatClient"/> class.</summary>
    /// <param name="httpClientFactory">Factory for outbound HTTP clients.</param>
    public DirectChatClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Runs one completion and returns the assistant message content.</summary>
    /// <param name="config">Plugin configuration (timeout, token-limit field).</param>
    /// <param name="url">The full chat-completions URL (used verbatim).</param>
    /// <param name="apiKey">Bearer key for the endpoint (blank if none).</param>
    /// <param name="model">The model id to request.</param>
    /// <param name="messages">The chat messages payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant content string.</returns>
    /// <exception cref="DirectChatException">When the endpoint errors or replies with an unexpected shape.</exception>
    public async Task<string> CompleteAsync(PluginConfiguration config, string url, string apiKey, string model, object[] messages, CancellationToken cancellationToken)
    {
        var tokenMode = (config.ChatTokenParameter ?? "auto").Trim().ToLowerInvariant();
        var key = url + "\n" + model + "\n" + tokenMode;
        var shape = Learned.TryGetValue(key, out var known) ? known : RequestShape.For(tokenMode);

        var (ok, status, body) = (false, 0, string.Empty);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            (ok, status, body) = await PostAsync(config, url, apiKey, BuildPayload(model, messages, shape), cancellationToken).ConfigureAwait(false);
            if (ok || (status != 400 && status != 422))
            {
                break;
            }

            var next = shape.AdjustFor(body, tokenMode == "auto");
            if (next is null)
            {
                break;
            }

            shape = next;
        }

        if (!ok)
        {
            throw new DirectChatException($"Chat endpoint returned {status}: {Truncate(body, 300)}");
        }

        Learned[key] = shape;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new DirectChatException($"Unexpected chat response: {Truncate(body, 300)}");
        }
    }

    // The token budget is the *combined* allowance for a reasoning model's
    // internal thinking and its visible answer. 900 was too small: a reasoning
    // model spent it all thinking and returned empty content. 4096 leaves room.
    private static Dictionary<string, object> BuildPayload(string model, object[] messages, RequestShape shape)
    {
        var payload = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
        if (shape.Temperature)
        {
            payload["temperature"] = 0.4;
        }

        if (shape.TokenField is not null)
        {
            payload[shape.TokenField] = TokenLimit;
        }

        if (shape.Json)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        return payload;
    }

    private async Task<(bool Ok, int Status, string Body)> PostAsync(
        PluginConfiguration config,
        string url,
        string apiKey,
        Dictionary<string, object> payload,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120));
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Which optional fields a chat request carries.</summary>
    /// <param name="TokenField">"max_tokens", "max_completion_tokens", or <c>null</c> for no limit.</param>
    /// <param name="Temperature">Whether to send a temperature.</param>
    /// <param name="Json">Whether to ask for JSON output with <c>response_format</c>.</param>
    internal sealed record RequestShape(string? TokenField, bool Temperature, bool Json)
    {
        /// <summary>The first shape tried for a token-limit setting.</summary>
        /// <param name="tokenMode">The configured token-limit field.</param>
        /// <returns>The starting shape.</returns>
        public static RequestShape For(string tokenMode) => tokenMode switch
        {
            "max_completion_tokens" => new RequestShape("max_completion_tokens", true, true),
            "none" => new RequestShape(null, true, true),
            _ => new RequestShape("max_tokens", true, true)
        };

        /// <summary>
        /// The shape to retry with after a rejection, based on which field the
        /// error message names, or <c>null</c> when there is nothing left to change.
        /// </summary>
        /// <param name="error">The error response body.</param>
        /// <param name="mayChangeTokenField">Whether the token field is on "auto".</param>
        /// <returns>The next shape, or <c>null</c>.</returns>
        public RequestShape? AdjustFor(string error, bool mayChangeTokenField)
        {
            var text = error.ToLowerInvariant();
            if (mayChangeTokenField && TokenField == "max_tokens" && text.Contains("max_tokens", StringComparison.Ordinal))
            {
                return this with { TokenField = "max_completion_tokens" };
            }

            if (mayChangeTokenField && TokenField == "max_completion_tokens" && text.Contains("max_completion_tokens", StringComparison.Ordinal))
            {
                return this with { TokenField = "max_tokens" };
            }

            if (Temperature && text.Contains("temperature", StringComparison.Ordinal))
            {
                return this with { Temperature = false };
            }

            // Anything else: the oldest fallback, plain text instead of JSON mode.
            return Json ? this with { Json = false } : null;
        }
    }
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
