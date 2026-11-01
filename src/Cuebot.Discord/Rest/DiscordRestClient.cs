using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cuebot.Discord;

/// <summary>A Discord REST call that failed, with a message a streamer can act on.</summary>
public sealed class DiscordApiException(string message, HttpStatusCode status, int code) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>Discord's JSON error code (50013 Missing Permissions...), 0 when none.</summary>
    public int Code { get; } = code;
}

/// <summary>
/// Discord REST API v10 with <c>Authorization: Bot &lt;token&gt;</c>. Honors the per-route rate limits Discord reports
/// (X-RateLimit-Remaining / -Reset-After, waiting before a call when a route's bucket is empty), retries 429 answers after
/// <c>retry_after</c> (route or global), and turns errors into plain messages. Thread-safe.
/// </summary>
public sealed partial class DiscordRestClient
{
    /// <summary>Discord asks bots for "DiscordBot ($url, $versionNumber)".</summary>
    public const string UserAgent = "DiscordBot (https://github.com/TechDaddyKB/cuebot, 1.0)";

    public static readonly Uri DefaultBase = new("https://discord.com/api/v10/");

    private const int MaxAttempts = 4;
    private readonly HttpClient _http;
    private readonly Func<string> _token;
    private readonly Uri _base;
    private readonly TimeSpan _maxWait;
    private readonly ConcurrentDictionary<string, RouteState> _routes = new(StringComparer.Ordinal);
    private long _globalUntilTicks;

    public DiscordRestClient(HttpClient http, Func<string> token, Uri? baseUri = null, TimeSpan? maxRateLimitWait = null)
    {
        _http = http;
        _token = token;
        _base = baseUri ?? DefaultBase;
        _maxWait = maxRateLimitWait ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Calls <paramref name="path"/> (relative, e.g. "channels/123/messages").</summary>
    /// <param name="needs">What the bot needs for this call, used in the message for 403 answers (e.g. "Send Messages").</param>
    /// <returns>The JSON answer, or null for 204 No Content.</returns>
    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, JsonNode? body, string? needs, CancellationToken ct,
        bool authorize = true, string? auditReason = null)
    {
        var token = _token().Trim();
        if (authorize && token.Length == 0) throw new InvalidOperationException("No Discord bot token saved. Paste it on the Discord bot page first.");
        var route = RouteKey(method, path);
        var state = _routes.GetOrAdd(route, _ => new RouteState());
        for (var attempt = 1; ; attempt++)
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                await WaitForLimitsAsync(state, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(method, new Uri(_base, path));
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (authorize) request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
                if (!string.IsNullOrWhiteSpace(auditReason))
                    request.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", Uri.EscapeDataString(auditReason.Trim()));
                if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                Remember(state, response);
            }
            finally
            {
                state.Gate.Release();
            }

            using (response)
            {
                var text = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    var (retryAfter, global) = RetryAfter(response, text);
                    if (global) Interlocked.Exchange(ref _globalUntilTicks, (DateTime.UtcNow + retryAfter).Ticks);
                    else state.BlockUntil(DateTime.UtcNow + retryAfter);
                    if (attempt >= MaxAttempts || retryAfter > _maxWait)
                        throw new DiscordApiException($"Discord is rate limiting the bot; try again in {Math.Ceiling(retryAfter.TotalSeconds):0} seconds.",
                            response.StatusCode, 0);
                    continue;
                }
                if ((int)response.StatusCode >= 500 && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw Explain(response.StatusCode, text, needs);
                if (response.StatusCode == HttpStatusCode.NoContent || text.Length == 0) return null;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
        }
    }

    public Task<JsonElement?> GetAsync(string path, CancellationToken ct, string? needs = null) => SendAsync(HttpMethod.Get, path, null, needs, ct);

    private async Task WaitForLimitsAsync(RouteState state, CancellationToken ct)
    {
        var global = new DateTime(Interlocked.Read(ref _globalUntilTicks), DateTimeKind.Utc) - DateTime.UtcNow;
        var route = state.WaitNeeded(DateTime.UtcNow);
        var wait = global > route ? global : route;
        if (wait <= TimeSpan.Zero) return;
        if (wait > _maxWait)
            throw new DiscordApiException($"Discord is rate limiting the bot; try again in {Math.Ceiling(wait.TotalSeconds):0} seconds.",
                (HttpStatusCode)429, 0);
        await Task.Delay(wait, ct).ConfigureAwait(false);
    }

    private static void Remember(RouteState state, HttpResponseMessage response)
    {
        if (Header(response, "X-RateLimit-Remaining") is { } remaining && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            && Header(response, "X-RateLimit-Reset-After") is { } after
            && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            state.Update(r, DateTime.UtcNow + TimeSpan.FromSeconds(seconds));
        }
    }

    private static (TimeSpan RetryAfter, bool Global) RetryAfter(HttpResponseMessage response, string text)
    {
        double seconds = 1;
        var global = string.Equals(Header(response, "X-RateLimit-Global"), "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var v)) seconds = v;
            if (doc.RootElement.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True) global = true;
        }
        catch (JsonException)
        {
            if (Header(response, "Retry-After") is { } h && double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) seconds = v;
        }
        return (TimeSpan.FromSeconds(Math.Max(0, seconds)), global);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A plain message for a failed call (docs: JSON Error Codes).</summary>
    internal static DiscordApiException Explain(HttpStatusCode status, string body, string? needs)
    {
        var code = 0;
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)) code = ci;
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
        }
        catch (JsonException)
        {
            // Not JSON (a proxy error page); fall back to the status.
        }
        var text = code switch
        {
            50013 => $"The bot is missing a permission{(needs is null ? "" : $": it needs {needs}")}. Give the bot's role that " +
                     "permission in Server Settings → Roles (or in the channel's Permissions).",
            50001 => $"The bot cannot see that channel. Give the bot's role \"View Channel\"{(needs is null ? "" : $" and {needs}")} there.",
            10003 => "Discord does not know that channel. It may have been deleted, or the bot is not in that server.",
            10008 => "Discord does not know that message. It may have been deleted already.",
            10007 => "That person is not a member of the server.",
            10011 => "Discord does not know that role. It may have been deleted.",
            10013 => "Discord does not know that user.",
            10004 => "The bot is not in that server. Invite it with the button on the Discord bot page.",
            10062 => "That slash command use is too old to answer (Discord allows 15 minutes).",
            50007 => "Discord does not let the bot message that person (they may block direct messages from server members).",
            50006 => "Discord does not allow an empty message. Type some text or fill in the embed.",
            50035 => $"Discord did not accept what was sent: {message}{Details(body)}",
            _ => status switch
            {
                HttpStatusCode.Unauthorized => "Discord did not accept the bot token. Reset it in the Developer Portal (Bot → Reset Token) and paste the new one.",
                HttpStatusCode.Forbidden => $"Discord refused: the bot is missing a permission{(needs is null ? "" : $" ({needs})")}.",
                HttpStatusCode.NotFound => "Discord could not find that (it may have been deleted).",
                _ => $"Discord answered {(int)status}{(message.Length > 0 ? ": " + message : "")}",
            },
        };
        return new DiscordApiException(text, status, code);
    }

    /// <summary>The first field error of a 50035 answer ("content: Must be 2000 or fewer in length.").</summary>
    private static string Details(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("errors", out var errors)) return "";
            return FirstError(errors, "") is { } found ? $" ({found})" : "";
        }
        catch (JsonException)
        {
            return "";
        }

        static string? FirstError(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            if (e.TryGetProperty("_errors", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                && list[0].TryGetProperty("message", out var msg))
                return $"{path.TrimStart('.')}: {msg.GetString()}";
            foreach (var p in e.EnumerateObject())
                if (FirstError(p.Value, path + "." + p.Name) is { } inner) return inner;
            return null;
        }
    }

    /// <summary>
    /// The rate-limit route of a call: method plus path, with ids replaced except the "major parameters" Discord limits
    /// separately (channel, guild, webhook id and token).
    /// </summary>
    internal static string RouteKey(HttpMethod method, string path)
    {
        var p = path.Split('?')[0].Trim('/');
        var parts = p.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var previous = i > 0 ? parts[i - 1] : "";
            var major = previous is "channels" or "guilds" or "webhooks" || (i >= 2 && parts[i - 2] == "webhooks");
            if (!major && Snowflake().IsMatch(parts[i])) parts[i] = ":id";
            // Interaction callbacks: interactions/{id}/{token}/callback — one route for all of them.
            if (previous == "interactions" || (i >= 2 && parts[i - 2] == "interactions")) parts[i] = ":id";
        }
        // Message deletes have their own limit; keep the method in the key.
        return method.Method + " " + string.Join('/', parts);
    }

    [GeneratedRegex(@"^\d{5,20}$")]
    private static partial Regex Snowflake();

    private sealed class RouteState
    {
        private readonly object _lock = new();
        private int _remaining = 1;
        private DateTime _resetAt = DateTime.MinValue;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Update(int remaining, DateTime resetAt)
        {
            lock (_lock)
            {
                _remaining = remaining;
                _resetAt = resetAt;
            }
        }

        public void BlockUntil(DateTime until)
        {
            lock (_lock)
            {
                _remaining = 0;
                if (until > _resetAt) _resetAt = until;
            }
        }

        public TimeSpan WaitNeeded(DateTime now)
        {
            lock (_lock)
            {
                if (now >= _resetAt) return TimeSpan.Zero;
                return _remaining > 0 ? TimeSpan.Zero : _resetAt - now;
            }
        }
    }
}
