using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Webhooks.Outgoing;

/// <summary>A failed outgoing webhook call. Messages never contain the webhook URL or key.</summary>
public sealed class OutgoingWebhookException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What Discord answered about a webhook message.</summary>
/// <param name="MessageId">The message id (empty if Discord did not return one).</param>
/// <param name="ChannelId">The channel, or the thread when the message is in one.</param>
/// <param name="InThread">The message went to a thread or started a forum post.</param>
public sealed record DiscordWebhookResult(string MessageId, string ChannelId, bool InThread);

/// <summary>
/// Posts, edits and deletes messages through a Discord channel webhook. A plain message is the same payload as
/// <c>CPH.DiscordPostTextToWebhook</c> (content, tts, optional username and avatar_url); JSON bodies add embeds and
/// link buttons. Sent with <c>wait=true</c> so Discord answers with the message id.
/// </summary>
public sealed partial class DiscordWebhookClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Discord's limit for <c>content</c>.</summary>
    public const int MaxContentLength = 2000;

    /// <summary>Discord's limit for a new forum post's title.</summary>
    public const int MaxThreadNameLength = 100;

    private const int MaxRateLimitRetries = 2;
    private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(10);

    /// <summary>Posts and returns the new message's id (empty if Discord did not return one).</summary>
    public async Task<string> PostAsync(string webhookUrl, string content, string? username = null, string? avatarUrl = null,
        bool textToSpeech = false, CancellationToken ct = default) =>
        (await PostAsync(webhookUrl, SimpleBody(content, username, avatarUrl, textToSpeech), null, null, ct).ConfigureAwait(false)).MessageId;

    /// <summary>The body of a plain text message, exactly as <c>CPH.DiscordPostTextToWebhook</c> sends it.</summary>
    public static DiscordWebhookBody SimpleBody(string content, string? username, string? avatarUrl, bool textToSpeech)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content is required.");
        if (content.Length > MaxContentLength) throw new ArgumentException($"Discord messages are limited to {MaxContentLength} characters (got {content.Length}).");
        var payload = new JsonObject();
        foreach (var (key, value) in BuildPayload(content, username, avatarUrl, textToSpeech))
            payload[key] = value is bool flag ? JsonValue.Create(flag) : JsonValue.Create(value as string);
        return new DiscordWebhookBody(payload, null, false);
    }

    /// <summary>Posts a message, optionally into a thread or as a new forum post.</summary>
    public async Task<DiscordWebhookResult> PostAsync(string webhookUrl, DiscordWebhookBody body, string? threadId, string? threadName,
        CancellationToken ct = default)
    {
        var url = ParseUrl(webhookUrl);
        var thread = ThreadOf(url, threadId, body.ThreadId);
        var title = threadName?.Trim() ?? "";
        if (title.Length > MaxThreadNameLength) throw new ArgumentException($"A forum post title is limited to {MaxThreadNameLength} characters (got {title.Length}).");
        if (title.Length > 0 && thread is not null) throw new ArgumentException("Use a thread ID or a new forum post title, not both.");
        if (title.Length > 0) body.Payload["thread_name"] = title;

        var response = await SendAsync(HttpMethod.Post, WithOptions(WithWait(url), thread, body.HasComponents), body.Payload.ToJsonString(), ct).ConfigureAwait(false);
        return new DiscordWebhookResult(MessageId(response), Field(response, "channel_id"), thread is not null || title.Length > 0);
    }

    /// <summary>Edits a message this webhook sent.</summary>
    public async Task<DiscordWebhookResult> EditAsync(string webhookUrl, string messageId, DiscordWebhookBody body, string? threadId,
        CancellationToken ct = default)
    {
        var url = ParseUrl(webhookUrl);
        var thread = ThreadOf(url, threadId, body.ThreadId);
        var response = await SendAsync(HttpMethod.Patch, MessageEndpoint(url, messageId, thread, body.HasComponents), body.Payload.ToJsonString(), ct)
            .ConfigureAwait(false);
        var id = MessageId(response);
        return new DiscordWebhookResult(id.Length > 0 ? id : messageId, Field(response, "channel_id"), thread is not null);
    }

    /// <summary>Deletes a message this webhook sent.</summary>
    public async Task DeleteAsync(string webhookUrl, string messageId, string? threadId, CancellationToken ct = default)
    {
        var url = ParseUrl(webhookUrl);
        await SendAsync(HttpMethod.Delete, MessageEndpoint(url, messageId, ThreadOf(url, threadId, null), false), null, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpMethod method, Uri uri, string? json, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new OutgoingWebhookException($"Could not reach Discord: {ex.HttpRequestError}.");   // inner left out: it may name the host
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = RetryAfter(response, body);
                    if (attempt < MaxRateLimitRetries && wait is { } pause && pause <= MaxRateLimitWait)
                    {
                        await (delay ?? Task.Delay)(pause, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new OutgoingWebhookException(wait is { } longer
                        ? $"Discord is rate limiting this webhook. Try again in {Math.Ceiling(longer.TotalSeconds):0} seconds."
                        : "Discord is rate limiting this webhook. Try again shortly.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    var reason = Field(body, "message");
                    throw new OutgoingWebhookException($"Discord refused the webhook message: HTTP {(int)response.StatusCode} {response.ReasonPhrase}." +
                                                       (reason.Length > 0 ? $" {reason[..Math.Min(reason.Length, 200)]}" : ""));
                }
                return body;
            }
        }
    }

    /// <summary>How long Discord asked to wait: <c>retry_after</c> in the body (seconds), else the Retry-After header.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("retry_after", out var value)
                && value.TryGetDouble(out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the header.
        }
        return response.Headers.RetryAfter?.Delta;
    }

    /// <summary>The thread to use: the step's, then a Discohook export's, then one already in the webhook URL.</summary>
    private static string? ThreadOf(Uri url, string? threadId, string? fromBody)
    {
        var thread = threadId?.Trim() is { Length: > 0 } typed ? typed : fromBody?.Trim() is { Length: > 0 } exported ? exported : QueryValue(url, "thread_id");
        if (thread is not null && !DiscordWebhookJson.IdPattern().IsMatch(thread)) throw new ArgumentException("Thread ID must be a number.");
        return thread;
    }

    private static string? QueryValue(Uri url, string name)
    {
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Equals(name, StringComparison.OrdinalIgnoreCase) && part[(equals + 1)..] is { Length: > 0 } value)
                return Uri.UnescapeDataString(value);
        }
        return null;
    }

    /// <summary>Sets <c>thread_id</c> and, when the body has components, <c>with_components=true</c>.</summary>
    internal static Uri WithOptions(Uri url, string? threadId, bool withComponents)
    {
        var parts = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("thread_id=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("with_components=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (threadId is not null) parts.Add("thread_id=" + threadId);
        if (withComponents) parts.Add("with_components=true");
        return new UriBuilder(url) { Query = string.Join('&', parts) }.Uri;
    }

    /// <summary><c>/webhooks/{id}/{token}/messages/{message}</c>, from a plain webhook URL.</summary>
    internal static Uri MessageEndpoint(Uri url, string messageId, string? threadId, bool withComponents)
    {
        if (!DiscordWebhookJson.IdPattern().IsMatch(messageId)) throw new ArgumentException("Message must be a Discord message id.");
        var path = url.AbsolutePath.TrimEnd('/');
        if (!WebhookPath().IsMatch(path))
            throw new ArgumentException("Webhook URL must be a plain Discord webhook URL (https://discord.com/api/webhooks/...).");
        var plain = new UriBuilder(url) { Path = $"{path}/messages/{messageId}", Query = "" }.Uri;
        return WithOptions(plain, threadId, withComponents);
    }

    [GeneratedRegex(@"^/api(/v\d+)?/webhooks/\d+/[^/]+$", RegexOptions.IgnoreCase)]
    private static partial Regex WebhookPath();

    private static string Field(string body, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>The JSON body, exactly as <c>CPH.DiscordPostTextToWebhook</c> sends it.</summary>
    internal static Dictionary<string, object?> BuildPayload(string content, string? username, string? avatarUrl, bool textToSpeech)
    {
        var payload = new Dictionary<string, object?> { ["content"] = content, ["tts"] = textToSpeech };
        if (!string.IsNullOrWhiteSpace(username)) payload["username"] = username.Trim();
        if (!string.IsNullOrWhiteSpace(avatarUrl)) payload["avatar_url"] = avatarUrl.Trim();
        return payload;
    }

    /// <summary>Accepts discord.com / discordapp.com (and canary/ptb) webhook URLs only.</summary>
    internal static Uri ParseUrl(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must be a Discord webhook URL (https://discord.com/api/webhooks/...).");
        var host = url.Host.ToLowerInvariant();
        var discord = host is "discord.com" or "discordapp.com" or "canary.discord.com" or "ptb.discord.com";
        if (!discord || !url.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.Contains("/webhooks/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Webhook URL must be a Discord webhook URL (https://discord.com/api/webhooks/...).");
        return url;
    }

    internal static Uri WithWait(Uri url)
    {
        if (url.Query.Contains("wait=", StringComparison.OrdinalIgnoreCase)) return url;
        var builder = new UriBuilder(url) { Query = url.Query.Length > 1 ? url.Query[1..] + "&wait=true" : "wait=true" };
        return builder.Uri;
    }

    internal static string MessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("id", out var id)
                ? id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText()
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>
/// Triggers IFTTT applets through IFTTT's Webhooks service (ifttt.com/maker_webhooks): <c>POST
/// https://maker.ifttt.com/trigger/{event}/with/key/{key}</c> with <c>value1</c>..<c>value3</c>, or
/// <c>…/trigger/{event}/json/with/key/{key}</c> with any JSON object (the applet sees it as <c>JsonPayload</c>).
/// Streamer.bot sends its IFTTT notifications through its own website service instead; Cuebot has none, so
/// applets use the "Webhooks → Receive a web request" trigger with the same event name.
/// </summary>
public sealed class IftttWebhookClient(HttpClient http, Uri? baseUri = null)
{
    public static readonly Uri DefaultBase = new("https://maker.ifttt.com/");

    /// <summary>How many arguments become <c>ArgumentNName</c> / <c>ArgumentNValue</c> ingredients, like Streamer.bot.</summary>
    public const int NamedArguments = 5;

    private readonly Uri _base = baseUri ?? DefaultBase;

    /// <summary>Notification (Basic): the text is sent as <c>value1</c>.</summary>
    public Task NotifyAsync(string key, string eventName, string text, CancellationToken ct = default) =>
        PostAsync(Endpoint(key, eventName, json: false), new Dictionary<string, object?> { ["value1"] = text }, ct);

    /// <summary>
    /// Notification (Advanced): <c>Argument1Name</c>/<c>Argument1Value</c> … for the first five arguments,
    /// <c>ArgumentsJson</c> with all of them, and <c>value1</c>..<c>value3</c> with the first three values.
    /// </summary>
    public Task NotifyAdvancedAsync(string key, string eventName, IReadOnlyList<KeyValuePair<string, string>> arguments,
        CancellationToken ct = default) =>
        PostAsync(Endpoint(key, eventName, json: true), BuildAdvancedPayload(arguments), ct);

    internal static Dictionary<string, object?> BuildAdvancedPayload(IReadOnlyList<KeyValuePair<string, string>> arguments)
    {
        var payload = new Dictionary<string, object?>();
        for (var i = 0; i < Math.Min(NamedArguments, arguments.Count); i++)
        {
            payload[$"Argument{i + 1}Name"] = arguments[i].Key;
            payload[$"Argument{i + 1}Value"] = arguments[i].Value;
        }
        for (var i = 0; i < Math.Min(3, arguments.Count); i++) payload[$"value{i + 1}"] = arguments[i].Value;
        var all = new Dictionary<string, string>();
        foreach (var (name, value) in arguments) all[name] = value;
        payload["ArgumentsJson"] = JsonSerializer.Serialize(all);
        return payload;
    }

    /// <summary>Arguments from "name=value" lines (blank lines and lines without a name are skipped).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseArguments(string text)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var equals = line.IndexOf('=');
            var name = (equals < 0 ? line : line[..equals]).Trim();
            if (name.Length == 0) continue;
            result.Add(new(name, equals < 0 ? "" : line[(equals + 1)..].Trim()));
        }
        return result;
    }

    internal Uri Endpoint(string key, string eventName, bool json)
    {
        key = key.Trim();
        eventName = eventName.Trim();
        if (key.Length == 0) throw new ArgumentException("An IFTTT Webhooks key is required (ifttt.com/maker_webhooks → Documentation).");
        if (eventName.Length == 0) throw new ArgumentException("Event name is required.");
        var path = string.Create(CultureInfo.InvariantCulture,
            $"trigger/{Uri.EscapeDataString(eventName)}/{(json ? "json/" : "")}with/key/{Uri.EscapeDataString(key)}");
        return new Uri(_base, path);
    }

    private async Task PostAsync(Uri endpoint, Dictionary<string, object?> payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(endpoint, payload, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OutgoingWebhookException($"Could not reach IFTTT: {ex.HttpRequestError}.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new OutgoingWebhookException((int)response.StatusCode is 401 or 403
                    ? "IFTTT refused the Webhooks key."
                    : $"IFTTT refused the notification: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }
}
