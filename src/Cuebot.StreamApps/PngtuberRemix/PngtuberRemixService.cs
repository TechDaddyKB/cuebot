using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.PngtuberRemix;

/// <summary>Bot event types published by <see cref="PngtuberRemixService"/>.</summary>
public static class PngtuberRemixEvents
{
    public const string Prefix = "pngtuberRemix";
    public const string Connected = "pngtuberRemix.connected";
    public const string Disconnected = "pngtuberRemix.disconnected";
}

/// <summary>An avatar state (1-based number, as PNGTuber Remix shows it) or a sprite PNGTuber Remix reported.</summary>
public sealed record PngtuberRemixState(int Id, string Name, bool IsCurrent);

public sealed record PngtuberRemixSprite(string Name, string Id, bool Visible, bool IsGroup);

/// <summary>
/// PNGTuber Remix through the WebSocket API it documents in-app (WebSocket tab; ws://127.0.0.1:9321). Requests are
/// <c>{"event":..}</c> and every one gets exactly one answer, in order, with the same "event" and a "result" of
/// "success" or "failed" (an unknown or malformed request is answered with "event":"error"). It pushes nothing.
/// </summary>
public sealed class PngtuberRemixService : StreamAppConnection
{
    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly ConcurrentQueue<TaskCompletionSource<JsonElement>> _requests = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private volatile IReadOnlyList<PngtuberRemixState> _states = [];
    private volatile IReadOnlyList<PngtuberRemixSprite> _sprites = [];

    public PngtuberRemixService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal PngtuberRemixService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("PNGTuber Remix", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public PngtuberRemixSettings Settings => _settings.Value.PngtuberRemix;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "127.0.0.1";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 9321;

    /// <summary>Avatar states read on connect and by <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<PngtuberRemixState> States => _states;

    /// <summary>Sprites (and groups: sprites with children) read on connect and by <see cref="RefreshAsync"/>.</summary>
    public IReadOnlyList<PngtuberRemixSprite> Sprites => _sprites;

    public IReadOnlyList<string> StateNames() => [.. _states.Select(s => s.Name)];

    public IReadOnlyList<string> SpriteNames() => [.. _sprites.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> GroupNames() => [.. _sprites.Where(s => s.IsGroup).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Raised (on a background thread) when the states or sprites were re-read.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private protected override (Uri Url, string Display) Endpoint() => (new Uri($"ws://{Host}:{Port}/"), $"PNGTuber Remix at {Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["pngtuberRemix.host"] = Host,
        ["pngtuberRemix.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args)
    {
        foreach (var (key, value) in ConnectionArgs()) args.TryAdd(key, value);
        args.TryAdd("service", Name);
        Bus.Publish(new BotEvent($"{PngtuberRemixEvents.Prefix}.{name}", new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)));
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            MarkConnected($"Connected to PNGTuber Remix at {Host}:{Port}");
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
            {
                Log.Warn(Name, $"Could not read the states and sprites: {ex.Message}");
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
        _states = [];
        _sprites = [];
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                if (_requests.TryDequeue(out var tcs)) tcs.TrySetResult(message.Clone());
            }
        }
        finally
        {
            while (_requests.TryDequeue(out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    // ---- Requests ----

    /// <summary>
    /// Sends one request and returns its answer. Answers carry no request id, so sending and queueing happen under
    /// one lock to keep them in the same order. Throws <see cref="StreamAppRequestException"/> when it failed.
    /// </summary>
    internal async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        EnsureConnected();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _requests.Enqueue(tcs);
            await SendAsync(JsonUtil.Serialize(request), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
        JsonElement answer;
        try
        {
            answer = await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("PNGTuber Remix did not answer in time.");
        }
        if (answer.Str("event") == "error")
            throw new StreamAppRequestException($"PNGTuber Remix refused the request: {answer.Str("message")}");
        if (answer.Str("result") == "failed")
        {
            var error = answer.Str("error") is { Length: > 0 } e ? e : "it failed";
            throw new StreamAppRequestException($"PNGTuber Remix: {error}{(answer.Str("identifier") is { Length: > 0 } id ? $" ({id})" : "")}.");
        }
        return answer;
    }

    /// <summary>Re-reads the avatar states and sprites.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PngtuberRemixState> states;
        try
        {
            states = ParseStates(await RequestAsync(new { @event = "list_states" }, ct).ConfigureAwait(false));
        }
        catch (StreamAppRequestException)
        {
            states = []; // "no state buttons found" while a model is loading.
        }
        var sprites = await RequestAsync(new { @event = "list_sprites" }, ct).ConfigureAwait(false);
        _states = states;
        _sprites = sprites.TryGetProperty("sprites", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => new PngtuberRemixSprite(s.Str("name"), s.Str("id"), s.Bool("visible") ?? false, s.Bool("is_group") ?? false))
                .Where(s => s.Name.Length > 0)]
            : [];
        if (IsConnected)
            SetStatus(Cuebot.Core.ConnectionState.Connected,
                $"Connected to PNGTuber Remix at {Host}:{Port}: {_states.Count} states, {_sprites.Count} sprites");
        DataChanged?.Invoke();
    }

    private static List<PngtuberRemixState> ParseStates(JsonElement answer) =>
        answer.TryGetProperty("states", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object)
                .Select(s => new PngtuberRemixState((int)(s.Num("id") ?? 0), s.Str("name"), s.Bool("is_current") ?? false))]
            : [];

    /// <summary>The current avatar state, asked fresh.</summary>
    public async Task<PngtuberRemixState?> GetStateAsync(CancellationToken ct = default)
    {
        var states = ParseStates(await RequestAsync(new { @event = "list_states" }, ct).ConfigureAwait(false));
        _states = states;
        DataChanged?.Invoke();
        return states.FirstOrDefault(s => s.IsCurrent);
    }

    /// <summary>Switches the avatar state, by name or by its number (1 = the first). Returns the state's name.</summary>
    public async Task<string> SetStateAsync(string state, CancellationToken ct = default)
    {
        var wanted = Steps.Require(state, "State");
        object request = int.TryParse(wanted, out var number) && !_states.Any(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ? new { @event = "state", state_id = number }
            : new { @event = "state", state_name = wanted };
        var answer = await RequestAsync(request, ct).ConfigureAwait(false);
        if (answer.Num("state_id") is { } id)
        {
            _states = [.. _states.Select(s => s with { IsCurrent = s.Id == (int)id })];
            DataChanged?.Invoke();
        }
        return answer.Str("state_name") is { Length: > 0 } name ? name : wanted;
    }

    /// <summary>
    /// Shows, hides or toggles a sprite, or a group (a sprite with its children). PNGTuber Remix matches the name or
    /// sprite id. Returns whether it is now visible, when known.
    /// </summary>
    public async Task<bool?> SetVisibilityAsync(string name, bool group, SwitchMode mode, CancellationToken ct = default)
    {
        var target = Steps.Require(name, group ? "Group" : "Sprite");
        var kind = group ? "group" : "sprite";
        var verb = mode switch { SwitchMode.On => "show", SwitchMode.Off => "hide", _ => "toggle" };
        var request = new Dictionary<string, object?> { ["event"] = $"{verb}_{kind}", [$"{kind}_name"] = target };
        var answer = await RequestAsync(request, ct).ConfigureAwait(false);
        return mode switch
        {
            SwitchMode.On => true,
            SwitchMode.Off => false,
            _ => answer.Str("visibility") switch { "visible" => true, "hidden" => false, _ => null },
        };
    }

    /// <summary>Shakes a sprite (strength in pixels, default 10) for some seconds (default 1).</summary>
    public Task ShakeAsync(string sprite, double? strength, double? seconds, CancellationToken ct = default) =>
        RequestAsync(new { @event = "shake_sprite", sprite_name = Steps.Require(sprite, "Sprite"), intensity = strength ?? 10, duration = seconds ?? 1 }, ct);

    /// <summary>Bounces a sprite (height in pixels, default 20) once over some seconds (default 0.5).</summary>
    public Task BounceAsync(string sprite, double? height, double? seconds, CancellationToken ct = default) =>
        RequestAsync(new { @event = "bounce_sprite", sprite_name = Steps.Require(sprite, "Sprite"), height = height ?? 20, duration = seconds ?? 0.5 }, ct);

    /// <summary>
    /// Moves a sprite to x/y and/or scales and rotates it, over some seconds; with <paramref name="returnAfter"/> it
    /// goes back that many seconds later. Values left null are not sent (position and size stay as they are).
    /// </summary>
    public async Task MoveAsync(string sprite, double? x, double? y, double? scale, double? rotation, double? seconds, double? returnAfter, CancellationToken ct = default)
    {
        var name = Steps.Require(sprite, "Sprite");
        if (x is null && y is null && scale is null && rotation is null)
            throw new ArgumentException("Set a position, a size or a rotation.");
        var reset = returnAfter is not null;
        if (x is not null || y is not null)
            await RequestAsync(new
            {
                @event = "move_sprite", sprite_name = name, x = x ?? 0, y = y ?? 0, duration = seconds ?? 1,
                reset, reset_delay = returnAfter ?? 0,
            }, ct).ConfigureAwait(false);
        if (scale is not null || rotation is not null)
            await RequestAsync(new
            {
                @event = "animate_sprite", sprite_name = name, scale = scale ?? 1, rotation = rotation ?? 0, duration = seconds ?? 1,
                reset, reset_delay = returnAfter ?? 0,
            }, ct).ConfigureAwait(false);
    }

    /// <summary>Throws items at the avatar: a named item from its throwables, or random ones when the name is empty.</summary>
    public Task ThrowAsync(string item, int? amount, CancellationToken ct = default)
    {
        if (amount is <= 0) throw new ArgumentException("Amount must be at least 1.");
        object request = item.Trim().Length > 0
            ? new { @event = "throw_item", item_name = item.Trim(), amount = amount ?? 1 }
            : amount is { } n ? new { @event = "throw_random", amount = n } : new { @event = "throw_random" };
        return RequestAsync(request, ct);
    }

    /// <summary>Pauses or resumes throwing (Toggle flips). Returns whether throwing is now paused.</summary>
    public async Task<bool?> SetThrowingPausedAsync(SwitchMode mode, CancellationToken ct = default)
    {
        object request = mode == SwitchMode.Toggle
            ? new { @event = "toggle_throwables_pause" }
            : new { @event = "toggle_throwables_pause", pause = mode == SwitchMode.On };
        return (await RequestAsync(request, ct).ConfigureAwait(false)).Bool("is_paused");
    }

    /// <summary>Opens a saved model (.pngRemix file) and re-reads its states and sprites.</summary>
    public async Task LoadModelAsync(string path, CancellationToken ct = default)
    {
        await RequestAsync(new { @event = "load_model", file_path = Steps.Require(path, "Model file") }, ct).ConfigureAwait(false);
        try
        {
            // The state buttons appear a moment after the model: re-read until they do (about 2 s at most).
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                if (_states.Count > 0) break;
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
        {
            Log.Warn(Name, $"Loaded the model but could not re-read it: {ex.Message}");
        }
    }
}
