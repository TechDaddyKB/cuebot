using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Animaze;

/// <summary>Bot event types published by <see cref="AnimazeService"/>.</summary>
public static class AnimazeEvents
{
    public const string Prefix = "animaze";
    public const string Connected = "animaze.connected";
    public const string Disconnected = "animaze.disconnected";
    public const string AvatarChanged = "animaze.avatarChanged";
}

/// <summary>Something of the current avatar Animaze can play: its number (what the API takes) and its name.</summary>
public sealed record AnimazeItem(int Index, string Name);

/// <summary>An emote: the name shown in Animaze and the item name the API takes.</summary>
public sealed record AnimazeEmote(string FriendlyName, string ItemName);

/// <summary>
/// Animaze through its documented WebSocket API (Settings > Animaze API, ws://127.0.0.1:9000): requests are
/// <c>{"action":..,"id":..}</c> and the answer repeats both, with "error" when it failed. Animaze pushes
/// <c>{"event":"AvatarChanged","new_avatar":..}</c> to every client.
/// </summary>
public sealed class AnimazeService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<string> _avatars = [];
    private volatile IReadOnlyList<string> _scenes = [];
    private volatile IReadOnlyList<AnimazeEmote> _emotes = [];
    private volatile IReadOnlyList<AnimazeItem> _specialActions = [];
    private volatile IReadOnlyList<AnimazeItem> _poses = [];
    private volatile IReadOnlyList<AnimazeItem> _idles = [];
    private volatile IReadOnlyList<AnimazeItem> _quickscenes = [];
    private volatile string _avatar = "";

    public AnimazeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal AnimazeService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("Animaze", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public AnimazeSettings Settings => _settings.Value.Animaze;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 9000;

    /// <summary>The avatar Animaze last reported loading (empty until it does).</summary>
    public string CurrentAvatar => _avatar;

    public IReadOnlyList<string> Avatars => _avatars;
    public IReadOnlyList<string> Scenes => _scenes;
    public IReadOnlyList<AnimazeEmote> Emotes => _emotes;
    public IReadOnlyList<AnimazeItem> SpecialActions => _specialActions;
    public IReadOnlyList<AnimazeItem> Poses => _poses;
    public IReadOnlyList<AnimazeItem> Idles => _idles;
    public IReadOnlyList<AnimazeItem> Quickscenes => _quickscenes;

    /// <summary>Raised (on a background thread) when the lists were re-read or the avatar changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private protected override (Uri Url, string Display) Endpoint() => (new Uri($"ws://{Host}:{Port}/"), $"Animaze at {Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["animaze.host"] = Host,
        ["animaze.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args) => PublishEvent($"{AnimazeEvents.Prefix}.{name}", args);

    private void PublishEvent(string type, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent(type, new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            MarkConnected($"Connected to Animaze at {Host}:{Port}");
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
            {
                Log.Warn(Name, $"Could not read the avatars and animations: {ex.Message}");
            }
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session over.
            }
        }
    }

    private protected override void OnSessionEnded()
    {
        _avatars = [];
        _scenes = [];
        _emotes = [];
        _specialActions = [];
        _poses = [];
        _idles = [];
        _quickscenes = [];
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                if (message.Str("event") is { Length: > 0 } name)
                {
                    OnEvent(name, message);
                    continue;
                }
                if (message.Str("id") is { Length: > 0 } id && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(message.Clone());
            }
        }
        finally
        {
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    private void OnEvent(string name, JsonElement message)
    {
        if (!string.Equals(name, "AvatarChanged", StringComparison.OrdinalIgnoreCase)) return;
        var avatar = message.Str("new_avatar");
        var previous = _avatar;
        _avatar = avatar;
        DataChanged?.Invoke();
        PublishEvent(AnimazeEvents.AvatarChanged, new() { ["avatar"] = avatar, ["previousAvatar"] = previous });
        // The animations belong to the avatar: re-read them in the background.
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAvatarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException or StreamAppNotConnectedException or OperationCanceledException)
            {
                Log.Warn(Name, $"Could not re-read the avatar's animations: {ex.Message}");
            }
        });
    }

    // ---- Requests ----

    /// <summary>Sends one action and waits for its answer. Throws <see cref="StreamAppRequestException"/> when Animaze reports an error.</summary>
    internal async Task<JsonElement> RequestAsync(string action, Dictionary<string, object?>? fields, CancellationToken ct)
    {
        EnsureConnected();
        var id = Guid.NewGuid().ToString("N");
        var request = new Dictionary<string, object?> { ["action"] = action, ["id"] = id };
        if (fields is not null) foreach (var (k, v) in fields) request[k] = v;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(JsonUtil.Serialize(request), ct).ConfigureAwait(false);
            var answer = await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
            if (answer.Str("error") is { Length: > 0 } error) throw new StreamAppRequestException($"Animaze: {error}.");
            return answer;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Animaze did not answer {action} in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Re-reads avatars, scenes, quickscenes and the current avatar's emotes and animations.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _avatars = Names(await RequestAsync("GetAvatars", null, ct).ConfigureAwait(false), "avatars");
        _scenes = Names(await RequestAsync("GetScenes", null, ct).ConfigureAwait(false), "scenes");
        _quickscenes = await TryItemsAsync("GetQuickscenes", ct).ConfigureAwait(false);
        await RefreshAvatarAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshAvatarAsync(CancellationToken ct)
    {
        var emotes = await RequestAsync("GetEmotes", null, ct).ConfigureAwait(false);
        _emotes = emotes.TryGetProperty("emotes", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new AnimazeEmote(e.Str("friendlyName"), e.Str("itemName"))).Where(e => e.ItemName.Length > 0)]
            : [];
        _specialActions = await TryItemsAsync("GetSpecialActions", ct).ConfigureAwait(false);
        _poses = await TryItemsAsync("GetPoses", ct).ConfigureAwait(false);
        _idles = await TryItemsAsync("GetIdleAnims", ct).ConfigureAwait(false);
        if (IsConnected)
            SetStatus(ConnectionState.Connected,
                $"Connected to Animaze at {Host}:{Port}: {_avatars.Count} avatars, {_emotes.Count} emotes, {_specialActions.Count} special actions");
        DataChanged?.Invoke();
    }

    private async Task<IReadOnlyList<AnimazeItem>> TryItemsAsync(string action, CancellationToken ct)
    {
        try
        {
            return Items(await RequestAsync(action, null, ct).ConfigureAwait(false));
        }
        catch (StreamAppRequestException)
        {
            return []; // e.g. an avatar without poses.
        }
    }

    private static List<string> Names(JsonElement answer, string property) =>
        answer.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(ItemName).Where(n => n.Length > 0)]
            : [];

    /// <summary>
    /// The first list in an answer as numbered items. Only the special-actions shape is documented
    /// (<c>[{"animName":..,"index":..}]</c>); plain names are numbered by position.
    /// </summary>
    internal static List<AnimazeItem> Items(JsonElement answer)
    {
        var list = answer.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (list.ValueKind != JsonValueKind.Array) return [];
        return [.. list.EnumerateArray().Select((item, position) => new AnimazeItem(
            item.ValueKind == JsonValueKind.Object && item.Num("index") is { } i ? (int)i : position, ItemName(item)))];
    }

    private static string ItemName(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString() ?? "",
        JsonValueKind.Object => new[] { "animName", "friendlyName", "name", "itemName" }.Select(k => item.Str(k)).FirstOrDefault(s => s.Length > 0) ?? "",
        _ => "",
    };

    /// <summary>Picks an item by its name or its number (as shown in the step's suggestions).</summary>
    internal static int Pick(IReadOnlyList<AnimazeItem> items, string wanted, string label)
    {
        var text = Steps.Require(wanted, label);
        var byName = items.FirstOrDefault(i => string.Equals(i.Name, text, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName.Index;
        return int.TryParse(text, out var n) && n >= 0
            ? n
            : throw new StreamAppRequestException($"Animaze has no {label.ToLowerInvariant()} called '{text}' for this avatar.");
    }

    public Task LoadAvatarAsync(string name, CancellationToken ct = default) =>
        RequestAsync("LoadAvatar", new() { ["name"] = Steps.Require(name, "Avatar") }, ct);

    public Task LoadSceneAsync(string name, CancellationToken ct = default) =>
        RequestAsync("LoadScene", new() { ["name"] = Steps.Require(name, "Scene") }, ct);

    /// <summary>Plays an emote, by the name shown in Animaze or its item name. Returns the item name.</summary>
    public async Task<string> TriggerEmoteAsync(string emote, CancellationToken ct = default)
    {
        var text = Steps.Require(emote, "Emote");
        var item = _emotes.FirstOrDefault(e => string.Equals(e.FriendlyName, text, StringComparison.OrdinalIgnoreCase))?.ItemName ?? text;
        await RequestAsync("TriggerEmote", new() { ["itemName"] = item }, ct).ConfigureAwait(false);
        return item;
    }

    public Task TriggerSpecialActionAsync(string action, CancellationToken ct = default) =>
        RequestAsync("TriggerSpecialAction", new() { ["index"] = Pick(_specialActions, action, "Special action") }, ct);

    public Task TriggerPoseAsync(string pose, CancellationToken ct = default) =>
        RequestAsync("TriggerPose", new() { ["index"] = Pick(_poses, pose, "Pose") }, ct);

    public Task TriggerIdleAsync(string idle, CancellationToken ct = default) =>
        RequestAsync("TriggerIdle", new() { ["index"] = Pick(_idles, idle, "Idle animation") }, ct);

    public Task LoadQuicksceneAsync(string quickscene, CancellationToken ct = default) =>
        RequestAsync("LoadQuickscene", new() { ["index"] = Pick(_quickscenes, quickscene, "Quickscene") }, ct);

    /// <summary>Turns Animaze's virtual camera on or off.</summary>
    public Task SetBroadcastAsync(bool on, CancellationToken ct = default) =>
        RequestAsync("Broadcast", new() { ["toggle"] = on }, ct);
}
