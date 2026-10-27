using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.StreamElements;

/// <summary>How an Astro session ended.</summary>
internal sealed record AstroSessionEnd(string Reason, bool Fatal = false, bool ReconnectNow = false);

/// <summary>
/// Client for the StreamElements Astro websocket gateway (wss://astro.streamelements.com): subscribes to topics
/// with the JWT, raises topic messages, follows <c>reconnect</c> hand-offs with their reconnect token (the server
/// restores subscriptions), and reconnects with backoff. Websocket PING frames are answered by the runtime.
/// </summary>
internal sealed class AstroClient(Func<IWebSocketTransport> transports, Uri endpoint, DonationsTimings timings, AppLog log)
{
    public const string Source = "StreamElements";

    private string _reconnectToken = "";

    public event Action? Connected;
    public event Action<string>? Subscribed;
    public event Action<string>? Disconnected;

    /// <summary>A subscription was refused (topic, error code, description).</summary>
    public event Action<string, string, string>? SubscriptionFailed;

    public event Action<string, JsonElement>? MessageReceived;

    public static string SubscribeMessage(string topic, string token, string nonce) => JsonSerializer.Serialize(new
    {
        type = "subscribe",
        nonce,
        data = new { topic, token, token_type = "jwt" },
    });

    public async Task RunAsync(string token, IReadOnlyList<string> topics, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            AstroSessionEnd end;
            var healthy = false;
            try
            {
                end = await RunSessionAsync(token, topics, () => healthy = true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                end = new AstroSessionEnd(Secrets.Redact(ex.Message, token));
            }
            if (healthy) delay = timings.ReconnectDelay;
            Disconnected?.Invoke(end.Reason);
            var wait = end.ReconnectNow ? TimeSpan.Zero : end.Fatal ? timings.MaxReconnectDelay : delay;
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
            if (!end.ReconnectNow && !end.Fatal) delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
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
        if (!resuming)
        {
            foreach (var topic in topics)
            {
                var nonce = Guid.NewGuid().ToString("N");
                pending[nonce] = topic;
                await transport.SendAsync(SubscribeMessage(topic, token, nonce), ct).ConfigureAwait(false);
            }
        }
        Connected?.Invoke();
        var failures = 0;
        while (true)
        {
            var next = await inbox.ReadAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            if (next.Text is null) return new AstroSessionEnd(transport.CloseReason ?? "connection closed");
            if (JsonReaders.TryParse(next.Text) is not { ValueKind: JsonValueKind.Object } message) continue;
            var data = message.Child("data");
            switch (message.Str("type"))
            {
                case "welcome":
                    onHealthy();
                    break;
                case "response":
                    var nonce = message.Str("nonce");
                    var topic = pending.Remove(nonce, out var t) ? t : data.Str("topic");
                    if (message.Str("error") is { Length: > 0 } error)
                    {
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
                        Subscribed?.Invoke(topic);
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
}
