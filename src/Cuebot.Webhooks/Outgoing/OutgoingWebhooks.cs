using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cuebot.Webhooks.Outgoing;

/// <summary>A failed outgoing webhook call. Messages never contain the webhook URL or key.</summary>
public sealed class OutgoingWebhookException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Posts messages to a Discord channel webhook (the same payload as <c>CPH.DiscordPostTextToWebhook</c>:
/// content, tts, optional username and avatar_url). Sent with <c>wait=true</c> so Discord answers with the message id.
/// </summary>
public sealed class DiscordWebhookClient(HttpClient http)
{
    /// <summary>Discord's limit for <c>content</c>.</summary>
    public const int MaxContentLength = 2000;

    /// <summary>Posts and returns the new message's id (empty if Discord did not return one).</summary>
    public async Task<string> PostAsync(string webhookUrl, string content, string? username = null, string? avatarUrl = null,
        bool textToSpeech = false, CancellationToken ct = default)
    {
        var url = ParseUrl(webhookUrl);
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content is required.");
        if (content.Length > MaxContentLength) throw new ArgumentException($"Discord messages are limited to {MaxContentLength} characters (got {content.Length}).");

        var payload = BuildPayload(content, username, avatarUrl, textToSpeech);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(WithWait(url), payload, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OutgoingWebhookException($"Could not reach Discord: {ex.HttpRequestError}.");   // inner left out: it may name the host
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new OutgoingWebhookException($"Discord refused the webhook message: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return MessageId(body);
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
