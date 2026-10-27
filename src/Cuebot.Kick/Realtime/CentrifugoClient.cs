using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core.Logging;
using Cuebot.Kick.Pusher;

namespace Cuebot.Kick.Realtime;

/// <summary>How a Centrifugo session ended and how soon to reconnect.</summary>
internal sealed record CentrifugoSessionEnd(string Reason, CentrifugoReconnect Reconnect);

internal enum CentrifugoReconnect
{
    /// <summary>Reconnect after the usual backoff (network errors, timeouts, 3000-3499 and 4000-4499).</summary>
    Backoff,

    /// <summary>Reconnect right away with a new token (the token expired).</summary>
    Immediately,

    /// <summary>3500-3999 and 4500-4999: the server says not to reconnect; retried only at the maximum delay.</summary>
    Fatal,
}

/// <summary>
/// A minimal Centrifugo (JSON protocol) client for kick.com's real-time server: gets credentials, connects with the
/// token, subscribes to public channels, answers the server's empty-object pings, refreshes the token before it
/// expires and reconnects with backoff. Each publication's <c>{"event", "data"}</c> envelope is raised as a
/// <see cref="PusherFrame"/>, the shape Kick's events had on Pusher, so <see cref="KickPusherDecoder"/> reads both.
/// </summary>
internal sealed class CentrifugoClient(
    Func<IPusherTransport> transports,
    Func<string, CancellationToken, Task<KickRealtimeCredentials>> credentials,
    Func<string, CancellationToken, Task<string>> refreshToken,
    KickTimings timings,
    AppLog log)
{
    private const int ConnectId = 1;

    /// <summary>Refresh this long before the token's ttl runs out.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? SubscriptionSucceeded;
    public event Action<PusherFrame>? Message;

    /// <summary>Keeps a connection open until <paramref name="ct"/> is cancelled. Never throws except for cancellation.</summary>
    public async Task RunAsync(IReadOnlyList<string> channels, CancellationToken ct)
    {
        var delay = timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            CentrifugoSessionEnd end;
            var established = false;
            try
            {
                end = await RunSessionAsync(channels, () => established = true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                end = new CentrifugoSessionEnd(ex.Message, CentrifugoReconnect.Backoff);
            }

            if (established) delay = timings.ReconnectDelay;
            Disconnected?.Invoke(end.Reason);
            var wait = end.Reconnect switch
            {
                CentrifugoReconnect.Immediately => TimeSpan.Zero,
                CentrifugoReconnect.Fatal => timings.MaxReconnectDelay,
                _ => delay,
            };
            if (end.Reconnect == CentrifugoReconnect.Fatal) log.Error("Kick", $"Realtime connection refused: {end.Reason}");
            else log.Warn("Kick", $"Realtime connection lost ({end.Reason}); reconnecting in {wait.TotalSeconds:0.#}s");
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (end.Reconnect == CentrifugoReconnect.Backoff)
                delay = TimeSpan.FromTicks(Math.Min(timings.MaxReconnectDelay.Ticks, delay.Ticks * 2));
        }
    }

    /// <summary>One connection from connect to loss.</summary>
    internal async Task<CentrifugoSessionEnd> RunSessionAsync(IReadOnlyList<string> channels, Action onEstablished, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString();
        var creds = await credentials(clientId, ct).ConfigureAwait(false);

        await using var transport = transports();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(timings.ConnectTimeout);
            try
            {
                await transport.ConnectAsync(creds.Url, connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new CentrifugoSessionEnd("connect timed out", CentrifugoReconnect.Backoff);
            }
        }

        var inbox = Channel.CreateUnbounded<string?>();
        using var stopPump = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, inbox.Writer, stopPump.Token);
        try
        {
            await transport.SendAsync(Command("connect", new { token = creds.Token, name = "js" }, ConnectId), ct).ConfigureAwait(false);
            var reply = await ReadReplyAsync(inbox.Reader, ConnectId, ct).ConfigureAwait(false);
            if (reply.End is { } failed) return failed;
            var connect = reply.Body!.Value;

            // The server pings ("{}") every `ping` seconds; silence for longer than that plus a margin means the link is dead.
            var ping = connect.Long("ping") is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : timings.MaxActivityTimeout;
            var silence = ping + timings.PongTimeout;
            var answerPings = connect.Prop("pong") is { ValueKind: JsonValueKind.True };
            var refreshAt = RefreshTime(connect);

            var nextId = ConnectId + 1;
            var subscriptions = new Dictionary<int, string>();
            foreach (var channel in channels)
            {
                subscriptions[nextId] = channel;
                await transport.SendAsync(Command("subscribe", new { channel, flag = 1 }, nextId++), ct).ConfigureAwait(false);
            }
            onEstablished();
            Connected?.Invoke(connect.Str("client"));

            var refreshId = 0;
            while (true)
            {
                var wait = silence;
                if (refreshAt is { } at && refreshId == 0)
                {
                    var untilRefresh = at - DateTimeOffset.UtcNow;
                    if (untilRefresh < wait) wait = untilRefresh < TimeSpan.Zero ? TimeSpan.Zero : untilRefresh;
                }
                var next = await ReadAsync(inbox.Reader, wait, ct).ConfigureAwait(false);
                if (next.TimedOut)
                {
                    if (refreshAt is { } due && refreshId == 0 && DateTimeOffset.UtcNow >= due)
                    {
                        refreshId = nextId++;
                        var token = await refreshToken(clientId, ct).ConfigureAwait(false);
                        await transport.SendAsync(Command("refresh", new { token }, refreshId), ct).ConfigureAwait(false);
                        continue;
                    }
                    return new CentrifugoSessionEnd("no ping from Kick's real-time server", CentrifugoReconnect.Backoff);
                }
                if (next.Text is null) return Closed(transport);

                foreach (var line in next.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (TryParse(line) is not { } message) continue;
                    var root = message.RootElement;
                    using (message)
                    {
                        if (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any())
                        {
                            if (answerPings) await transport.SendAsync("{}", ct).ConfigureAwait(false);
                            continue;
                        }
                        if (root.Prop("push") is { } push)
                        {
                            if (push.Prop("disconnect") is { } disconnect)
                                return new CentrifugoSessionEnd($"Kick's real-time server disconnected ({disconnect.Long("code")} {disconnect.Str("reason")})",
                                    Classify((int)disconnect.Long("code")));
                            if (push.Prop("pub") is { } pub && ToFrame(push.Str("channel"), pub) is { } frame) Message?.Invoke(frame);
                            continue;
                        }
                        var id = (int)root.Long("id");
                        if (id == refreshId && refreshId != 0)
                        {
                            if (root.Prop("error") is { } refreshError) return ErrorEnd(refreshError);
                            refreshAt = RefreshTime(root.Prop("refresh"));
                            refreshId = 0;
                        }
                        else if (subscriptions.Remove(id, out var channel))
                        {
                            if (root.Prop("error") is { } error)
                                log.Warn("Kick", $"Could not subscribe to {channel}: {error.Long("code")} {error.Str("message")}");
                            else SubscriptionSucceeded?.Invoke(channel);
                        }
                    }
                }
            }
        }
        finally
        {
            await stopPump.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The pump reports failures through the inbox; nothing more to do while tearing down.
            }
        }
    }

    /// <summary>A publication's <c>{"event": ..., "data": ...}</c> envelope as a frame; null when it has no event name.</summary>
    internal static PusherFrame? ToFrame(string channel, JsonElement pub)
    {
        var envelope = pub.Prop("data");
        if (envelope is { ValueKind: JsonValueKind.String } encoded)
        {
            try
            {
                using var doc = JsonDocument.Parse(encoded.GetString() ?? "");
                return ToFrame(channel, doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return envelope is { ValueKind: JsonValueKind.Object } env ? ToFrame(channel, env) : null;

        static PusherFrame? ToFrame(string channel, JsonElement env)
        {
            if (env.Prop("event") is not { ValueKind: JsonValueKind.String } ev) return null;
            var data = env.Prop("data") switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
                { } other => other.GetRawText(),
                null => "",
            };
            return new PusherFrame(ev.GetString()!, channel.Length > 0 ? channel : null, data);
        }
    }

    internal static string Command(string method, object args, int id) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { [method] = args, ["id"] = id });

    private static DateTimeOffset? RefreshTime(JsonElement? result) =>
        result is { } r && r.Prop("expires") is { ValueKind: JsonValueKind.True } && r.Long("ttl") is > 0 and var ttl
            ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(1, ttl - RefreshMargin.TotalSeconds))
            : null;

    private readonly record struct Reply(JsonElement? Body, CentrifugoSessionEnd? End);

    /// <summary>Waits for the reply to command <paramref name="id"/>, answering pings meanwhile.</summary>
    private async Task<Reply> ReadReplyAsync(ChannelReader<string?> inbox, int id, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timings.EstablishTimeout;
        while (true)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            var next = await ReadAsync(inbox, left > TimeSpan.Zero ? left : TimeSpan.Zero, ct).ConfigureAwait(false);
            if (next.TimedOut) return new Reply(null, new CentrifugoSessionEnd("no connect reply from Kick's real-time server", CentrifugoReconnect.Backoff));
            if (next.Text is null) return new Reply(null, new CentrifugoSessionEnd("Kick's real-time server closed the connection during connect", CentrifugoReconnect.Backoff));
            foreach (var line in next.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParse(line) is not { } doc) continue;
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.Long("id") != id) continue;
                    if (root.Prop("error") is { } error) return new Reply(null, ErrorEnd(error));
                    return new Reply(root.Prop("connect") is { } body ? body.Clone() : default(JsonElement), null);
                }
            }
        }
    }

    private static async Task PumpAsync(IPusherTransport transport, ChannelWriter<string?> inbox, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) inbox.TryWrite(text);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Treated as a closed connection below.
        }
        inbox.TryWrite(null);
    }

    private readonly record struct ReadResult(bool TimedOut, string? Text);

    private static async Task<ReadResult> ReadAsync(ChannelReader<string?> reader, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return new ReadResult(false, await reader.ReadAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReadResult(true, null);
        }
        catch (ChannelClosedException)
        {
            return new ReadResult(false, null);
        }
    }

    private JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            log.Debug("Kick", "Ignoring a malformed realtime message");
            return null;
        }
    }

    /// <summary>Centrifugo error replies: 109 = token expired (get a new one now); others back off.</summary>
    internal static CentrifugoSessionEnd ErrorEnd(JsonElement error)
    {
        var code = (int)error.Long("code");
        var reason = $"Kick real-time error {code}: {error.Str("message")}";
        return new CentrifugoSessionEnd(reason, code == 109 ? CentrifugoReconnect.Immediately : CentrifugoReconnect.Backoff);
    }

    private static CentrifugoSessionEnd Closed(IPusherTransport transport) =>
        new(transport.CloseReason ?? "connection closed", Classify(transport.CloseCode ?? 0));

    /// <summary>Centrifugo close codes: 3500-3999 and 4500-4999 mean "do not reconnect"; the rest reconnect.</summary>
    internal static CentrifugoReconnect Classify(int code) => code switch
    {
        >= 3500 and < 4000 => CentrifugoReconnect.Fatal,
        >= 4500 and < 5000 => CentrifugoReconnect.Fatal,
        _ => CentrifugoReconnect.Backoff,
    };
}
