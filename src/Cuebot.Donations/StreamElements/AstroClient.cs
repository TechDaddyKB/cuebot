using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.StreamElements;

/// <summary>How an Astro session ended.</summary>
internal sealed record AstroSessionEnd(string Reason, bool Fatal = false, bool ReconnectNow = false);

/// <summary>
/// Client for the StreamElements Astro websocket gateway (wss://astro.streamelements.com): subscribes to topics
/// with the token once the server's <c>welcome</c> arrives (in the token's channel room when it is known), retries
/// subscriptions Astro refuses only for now (<c>err_deadline_exceeded</c>, <c>rate_limit_exceeded</c>, the latter with a
/// growing wait), raises topic messages, follows <c>reconnect</c> hand-offs with their reconnect token (the server
/// restores subscriptions), and reconnects with backoff: at once when an edge is draining (HTTP 502), after
/// <c>Retry-After</c> when connecting too fast (HTTP 429). Websocket PING frames are answered by the runtime.
/// </summary>
internal sealed class AstroClient(Func<IWebSocketTransport> transports, Uri endpoint, DonationsTimings timings, AppLog log)
{
    public const string Source = "StreamElements";

    /// <summary>Astro's command limit is one subscribe/unsubscribe per 100 ms.</summary>
    private static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(110);

    /// <summary>Docs: a 429 carries <c>Retry-After: 2</c>; never retry sooner than that.</summary>
    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>Retries of temporarily refused subscriptions per session.</summary>
    private const int MaxRetries = 5;

    private string _reconnectToken = "";

    /// <summary>Room sent with each subscribe (the token's channel id); empty leaves it out.</summary>
    public string Room { get; init; } = "";

    /// <summary><c>jwt</c> (default), <c>apikey</c> (Overlay Token) or <c>oauth2</c>.</summary>
    public string TokenType { get; init; } = "jwt";

    /// <summary>The canonical room the last successful subscribe response named.</summary>
    public string CanonicalRoom { get; private set; } = "";

    /// <summary>Wait before the next attempt when the gateway is draining (HTTP 502); tests shorten it.</summary>
    public TimeSpan DrainingRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Lower bound for a 429's Retry-After; tests shorten it.</summary>
    public TimeSpan MinRateLimitWait { get; init; } = MinRetryAfter;

    public event Action? Connected;

    /// <summary>A topic is subscribed (topic, canonical room; empty when restored by a resumed session).</summary>
    public event Action<string, string>? Subscribed;

    /// <summary>The server greeted a session; true when it resumed one with a reconnect token.</summary>
    public event Action<bool>? Welcomed;

    public event Action<string>? Disconnected;

    /// <summary>A subscription was refused (topic, error code, description).</summary>
    public event Action<string, string, string>? SubscriptionFailed;

    public event Action<string, JsonElement>? MessageReceived;

    public static string SubscribeMessage(string topic, string token, string nonce, string room = "", string tokenType = "jwt") =>
        room.Length > 0
            ? JsonSerializer.Serialize(new { type = "subscribe", nonce, data = new { topic, room, token, token_type = tokenType } })
            : JsonSerializer.Serialize(new { type = "subscribe", nonce, data = new { topic, token, token_type = tokenType } });

    public async Task RunAsync(string token, IReadOnlyList<string> topics, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            AstroSessionEnd end;
            var healthy = false;
            TimeSpan? overrideWait = null;
            try
            {
                end = await RunSessionAsync(token, topics, () => healthy = true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketUpgradeException ex) when (ex.Status is 502 or 429)
            {
                // 502: this edge is draining, another one takes the next connection. 429: connecting too fast.
                overrideWait = ex.Status == 502 ? DrainingRetryDelay
                    : ex.RetryAfter is { } after && after > MinRateLimitWait ? after : MinRateLimitWait;
                end = new AstroSessionEnd(ex.Status == 502 ? "the gateway is restarting (HTTP 502)" : "too many connections (HTTP 429)");
            }
            catch (Exception ex)
            {
                end = new AstroSessionEnd(Secrets.Redact(ex.Message, token));
            }
            if (healthy) delay = timings.ReconnectDelay;
            Disconnected?.Invoke(end.Reason);
            var wait = overrideWait ?? (end.ReconnectNow ? TimeSpan.Zero : end.Fatal ? timings.MaxReconnectDelay : delay);
            if (end.Fatal) log.Error(Source, $"Astro refused the connection: {end.Reason}");
            else log.Warn(Source, $"Astro connection lost ({end.Reason}); reconnecting in {wait.TotalSeconds:0.#}s");
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (overrideWait is null && !end.ReconnectNow && !end.Fatal) delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
        }
    }

    internal async Task<AstroSessionEnd> RunSessionAsync(string token, IReadOnlyList<string> topics, Action onHealthy, CancellationToken ct)
    {
        var resuming = _reconnectToken.Length > 0;
        var url = resuming ? new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/?reconnect_token={Uri.EscapeDataString(_reconnectToken)}") : endpoint;
        _reconnectToken = "";
        await using var transport = transports();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(timings.ConnectTimeout);
            try
            {
                await transport.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AstroSessionEnd("connect timed out");
            }
        }

        await using var inbox = new TransportInbox(transport, ct);
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        Connected?.Invoke();
        // Astro sends "welcome" right after the upgrade and the documented clients subscribe on it. A resumed session's
        // subscriptions are restored by the server, so its welcome means they are back.
        var subscribed = resuming;
        var welcomed = false;
        var openDeadline = DateTime.UtcNow + timings.OpenTimeout;
        var failures = 0;
        var retries = 0;
        while (true)
        {
            var timeout = subscribed ? Timeout.InfiniteTimeSpan : openDeadline - DateTime.UtcNow;
            var next = await inbox.ReadAsync(timeout, ct).ConfigureAwait(false);
            if (next.TimedOut)
            {
                // No welcome in time: subscribe anyway rather than sit idle.
                await SubscribeAsync(transport, topics, token, pending, ct).ConfigureAwait(false);
                subscribed = true;
                continue;
            }
            if (next.Text is null) return new AstroSessionEnd(transport.CloseReason ?? "connection closed");
            if (JsonReaders.TryParse(next.Text) is not { ValueKind: JsonValueKind.Object } message) continue;
            var data = message.Child("data");
            switch (message.Str("type"))
            {
                case "welcome":
                    onHealthy();
                    if (welcomed) break;
                    welcomed = true;
                    Welcomed?.Invoke(resuming);
                    if (resuming)
                    {
                        foreach (var restored in topics) Subscribed?.Invoke(restored, CanonicalRoom);
                    }
                    else if (!subscribed)
                    {
                        await SubscribeAsync(transport, topics, token, pending, ct).ConfigureAwait(false);
                        subscribed = true;
                    }
                    break;
                case "response":
                    var nonce = message.Str("nonce");
                    var topic = pending.Remove(nonce, out var t) ? t : data.Str("topic");
                    if (message.Str("error") is { Length: > 0 } error)
                    {
                        // "Retry the request" (err_deadline_exceeded) and the command rate limit (no nonce, so every
                        // unanswered subscription is sent again) are temporary.
                        if (error is "err_deadline_exceeded" or "rate_limit_exceeded" && retries < MaxRetries)
                        {
                            retries++;
                            var again = error == "rate_limit_exceeded" && nonce.Length == 0
                                ? pending.Values.Distinct(StringComparer.Ordinal).ToArray()
                                : topic.Length > 0 ? [topic] : [];
                            if (error == "rate_limit_exceeded" && nonce.Length == 0) pending.Clear();
                            // The command limit is 1 per 100 ms with a burst of 100 per 10 s: back off from 5 s and double,
                            // so a reconnect storm cannot burn every retry inside one window.
                            var backoff = error == "rate_limit_exceeded"
                                ? TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, timings.RateLimitBackoff.Ticks << (retries - 1)))
                                : timings.ReconnectDelay;
                            log.Warn(Source, $"Astro answered {error}; subscribing to {string.Join(", ", again)} again in {backoff.TotalSeconds:0.#}s");
                            await Task.Delay(backoff, ct).ConfigureAwait(false);
                            await SubscribeAsync(transport, again, token, pending, ct).ConfigureAwait(false);
                            break;
                        }
                        failures++;
                        var description = data.Str("message");
                        SubscriptionFailed?.Invoke(topic, error, description);
                        log.Warn(Source, $"Could not subscribe to {topic}: {error} {description}".TrimEnd());
                        if (error == "err_unauthorized" && failures >= topics.Count)
                            return new AstroSessionEnd($"StreamElements rejected the JWT token ({description})".Replace(" ()", ""), Fatal: true);
                    }
                    else
                    {
                        onHealthy();
                        // "The canonical room ID returned by the authorization service, which may differ from the
                        // requested value": kept for status and for any later unsubscribe.
                        if (data.Str("room") is { Length: > 0 } canonical) CanonicalRoom = canonical;
                        Subscribed?.Invoke(topic, data.Str("room"));
                    }
                    break;
                case "message":
                    onHealthy();
                    if (data is { } payload)
                    {
                        try
                        {
                            MessageReceived?.Invoke(message.Str("topic"), payload);
                        }
                        catch (Exception ex)
                        {
                            log.Error(Source, "Error handling an Astro message", ex);
                        }
                    }
                    break;
                case "reconnect":
                    _reconnectToken = data.Str("reconnect_token") is { Length: > 0 } rt ? rt : message.Str("reconnect_token");
                    return new AstroSessionEnd("server asked to reconnect", ReconnectNow: true);
            }
        }
    }

    /// <summary>One subscribe per topic; Astro allows one command per 100 ms.</summary>
    private async Task SubscribeAsync(IWebSocketTransport transport, IReadOnlyList<string> topics, string token,
        Dictionary<string, string> pending, CancellationToken ct)
    {
        for (var i = 0; i < topics.Count; i++)
        {
            if (i > 0) await Task.Delay(CommandSpacing, ct).ConfigureAwait(false);
            var nonce = Guid.NewGuid().ToString("N");
            pending[nonce] = topics[i];
            await transport.SendAsync(SubscribeMessage(topics[i], token, nonce, Room, TokenType), ct).ConfigureAwait(false);
        }
    }
}
