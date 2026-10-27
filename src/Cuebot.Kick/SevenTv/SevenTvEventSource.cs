using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Logging;
using Cuebot.Kick.Events;
using Cuebot.Kick.Pusher;

namespace Cuebot.Kick.SevenTv;

/// <summary>
/// 7TV emote changes for the Kick broadcaster, in real time from 7TV's public EventAPI (events.7tv.io/v3, see
/// PROTOCOL.md §7). Looks up the channel's active 7TV emote set (7tv.io/v3/users/kick/{kick user id}), subscribes
/// to <c>emote_set.update</c> for it and reports each emote pushed into / pulled from the set as
/// <see cref="KickEventTypes.SevenTvEmoteAdded"/> / <see cref="KickEventTypes.SevenTvEmoteRemoved"/>.
/// Reconnects with backoff; the set id is looked up again on every connection (the streamer may switch sets).
/// </summary>
internal sealed class SevenTvEventSource : IKickEventSource, IAsyncDisposable
{
    public const string Source = "7tv";

    // EventAPI v3 opcodes.
    private const int OpDispatch = 0;
    private const int OpHello = 1;
    private const int OpHeartbeat = 2;
    private const int OpReconnect = 4;
    private const int OpAck = 5;
    private const int OpError = 6;
    private const int OpEndOfStream = 7;
    private const int OpSubscribe = 35;

    private readonly Func<IPusherTransport> _transports;
    private readonly HttpClient _http;
    private readonly SevenTvEndpoints _endpoints;
    private readonly KickTimings _timings;
    private readonly AppLog _log;
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;
    private volatile bool _connected;

    public SevenTvEventSource(Func<IPusherTransport> transports, HttpClient http, SevenTvEndpoints endpoints, KickTimings timings, AppLog log)
    {
        _transports = transports;
        _http = http;
        _endpoints = endpoints;
        _timings = timings;
        _log = log;
    }

    public string Name => "7TV emotes";
    public bool IsConnected => _connected;

    /// <summary>The 7TV emote set being watched, once known.</summary>
    public string? EmoteSetId { get; private set; }

    /// <summary>Why the last connection attempt failed, when it did.</summary>
    public string? Problem { get; private set; }

    public event Action<KickEvent>? EventReceived;
    public event Action<bool, string>? ConnectionChanged;

    /// <summary>Raised after an emote change was reported (so the emote cache can be refreshed).</summary>
    public event Action? EmoteSetChanged;

    /// <summary>Starts (or restarts) watching the 7TV emote set of Kick user <paramref name="kickUserId"/>. Returns immediately.</summary>
    public async Task StartAsync(long kickUserId)
    {
        await StopAsync().ConfigureAwait(false);
        if (kickUserId <= 0) return;
        var stop = new CancellationTokenSource();
        var token = stop.Token;
        _stop = stop;
        _run = Task.Run(() => RunAsync(kickUserId, token));
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop is null) return;
        _stop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        stop.Dispose();
        SetConnected(false, "stopped");
    }

    private async Task RunAsync(long kickUserId, CancellationToken ct)
    {
        var delay = _timings.ReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            var wait = delay;
            try
            {
                var setId = await GetEmoteSetIdAsync(kickUserId, ct).ConfigureAwait(false);
                if (setId is null)
                {
                    Problem = "This Kick channel has no 7TV emote set (connect Kick on 7tv.app)";
                    wait = _timings.MaxReconnectDelay;
                }
                else
                {
                    EmoteSetId = setId;
                    Problem = null;
                    if (await SessionAsync(setId, ct).ConfigureAwait(false)) delay = _timings.ReconnectDelay;
                    wait = delay;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem = ex.Message;
                _log.Debug("Kick", $"7TV events: {ex.Message}");
            }
            SetConnected(false, Problem ?? "disconnected");
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>The id of the Kick user's active 7TV emote set, or null when they have no 7TV account / set.</summary>
    internal async Task<string?> GetEmoteSetIdAsync(long kickUserId, CancellationToken ct)
    {
        var url = new Uri(_endpoints.Api, "users/kick/" + kickUserId.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"7TV answered {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var id = doc.RootElement.Prop("emote_set").Str("id");
        if (id.Length == 0) id = doc.RootElement.Str("emote_set_id");
        return id.Length > 0 ? id : null;
    }

    /// <summary>One connection. Returns true when it got as far as a successful subscription.</summary>
    private async Task<bool> SessionAsync(string setId, CancellationToken ct)
    {
        await using var transport = _transports();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(_timings.ConnectTimeout);
            await transport.ConnectAsync(_endpoints.Events, connectTimeout.Token).ConfigureAwait(false);
        }

        var hello = await ReceiveAsync(transport, _timings.EstablishTimeout, ct).ConfigureAwait(false)
                    ?? throw new IOException("7TV closed the connection before saying hello");
        if (Op(hello.RootElement) != OpHello) throw new IOException("7TV did not say hello");
        var heartbeat = TimeSpan.FromMilliseconds(Math.Max(1000, hello.RootElement.Prop("d").Long("heartbeat_interval")));
        hello.Dispose();

        var subscribe = JsonSerializer.Serialize(new
        {
            op = OpSubscribe,
            d = new { type = "emote_set.update", condition = new Dictionary<string, string> { ["object_id"] = setId } },
        });
        await transport.SendAsync(subscribe, ct).ConfigureAwait(false);
        SetConnected(true, "connected");

        // 7TV sends a heartbeat every interval; three missed ones mean the connection is dead.
        var silence = heartbeat * 3 < _timings.MaxActivityTimeout ? heartbeat * 3 : _timings.MaxActivityTimeout;
        while (!ct.IsCancellationRequested)
        {
            using var message = await ReceiveAsync(transport, silence, ct).ConfigureAwait(false);
            if (message is null) return true;
            var root = message.RootElement;
            switch (Op(root))
            {
                case OpDispatch:
                    Dispatch(root.Prop("d"), setId);
                    break;
                case OpReconnect or OpEndOfStream:
                    _log.Debug("Kick", "7TV asked to reconnect");
                    return true;
                case OpError:
                    _log.Warn("Kick", $"7TV event error: {root.Prop("d").Str("message")}");
                    break;
                case OpHeartbeat or OpAck:
                    break;
            }
        }
        return true;
    }

    private void Dispatch(JsonElement? d, string setId)
    {
        if (d.Str("type") != "emote_set.update") return;
        var body = d.Prop("body");
        if (body is null) return;
        if (body.Str("id") is { Length: > 0 } id && id != setId) return;
        var events = Decode(body.Value, setId);
        foreach (var e in events)
        {
            try
            {
                EventReceived?.Invoke(e);
            }
            catch (Exception ex)
            {
                _log.Error("Kick", "7TV event handler failed", ex);
            }
        }
        if (events.Count > 0) EmoteSetChanged?.Invoke();
    }

    /// <summary>Emotes added (<c>pushed</c>) and removed (<c>pulled</c>) in an <c>emote_set.update</c> change map.</summary>
    internal static IReadOnlyList<KickEvent> Decode(JsonElement body, string setId)
    {
        var actor = body.Prop("actor");
        var result = new List<KickEvent>();
        foreach (var change in body.Items("pushed"))
            if (change.Str("key") == "emotes" && change.Prop("value") is { } value)
                result.Add(Event(true, value, actor, setId));
        foreach (var change in body.Items("pulled"))
            if (change.Str("key") == "emotes" && change.Prop("old_value") is { } old)
                result.Add(Event(false, old, actor, setId));
        return result;
    }

    private static KickEvent Event(bool added, JsonElement emote, JsonElement? actor, string setId)
    {
        var args = KickArgs.New(Source);
        var id = emote.Str("id");
        var name = emote.Str("name");
        args["emoteId"] = id;
        args["emoteName"] = name;
        args["emote"] = name;
        args["provider"] = Core.Chat.Emotes.EmoteProviders.SevenTv;
        args["emoteSetId"] = setId;
        args["imageUrl"] = ImageUrl(id, emote.Prop("data"));
        args["isAnimated"] = emote.Prop("data").Bool("animated");
        // Deliberately not "user"/"userId": the 7TV account is not a Kick user, and the hub maps those to Kick ids.
        args["sevenTvUser"] = actor.Str("display_name") is { Length: > 0 } display ? display : actor.Str("username");
        args["sevenTvUserName"] = actor.Str("username");
        args["sevenTvUserId"] = actor.Str("id");
        return new KickEvent(added ? KickEventTypes.SevenTvEmoteAdded : KickEventTypes.SevenTvEmoteRemoved, args,
            DedupeKey(id, name));
    }

    /// <summary>Same key for the same emote whether the EventAPI or an emote-cache refresh reported it.</summary>
    internal static string DedupeKey(string emoteId, string name) => $"7tv:{emoteId}:{name}";

    internal static string ImageUrl(string id, JsonElement? data)
    {
        var host = data.Prop("host").Str("url");
        if (host.StartsWith("//", StringComparison.Ordinal)) host = "https:" + host;
        if (host.Length == 0) host = $"https://cdn.7tv.app/emote/{id}";
        return host.TrimEnd('/') + "/1x.webp";
    }

    private static int Op(JsonElement root) => (int)root.Long("op");

    private static async Task<JsonDocument?> ReceiveAsync(IPusherTransport transport, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        string? text;
        try
        {
            text = await transport.ReceiveAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("No message from 7TV in time");
        }
        if (text is null) return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{\"op\":-1}");
        }
    }

    private void SetConnected(bool connected, string reason)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected, reason);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>7TV hosts: REST API (users, emote sets) and the EventAPI websocket.</summary>
internal sealed record SevenTvEndpoints(Uri Api, Uri Events)
{
    public static SevenTvEndpoints Production { get; } = new(new Uri("https://7tv.io/v3/"), new Uri("wss://events.7tv.io/v3"));
}
