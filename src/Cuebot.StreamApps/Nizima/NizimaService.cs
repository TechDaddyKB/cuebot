using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Nizima;

/// <summary>Bot event types published by <see cref="NizimaService"/>.</summary>
public static class NizimaEvents
{
    public const string Prefix = "nizima";
    public const string Connected = "nizima.connected";
    public const string Disconnected = "nizima.disconnected";

    /// <summary>The selected model changed (the one steps act on when they name no model).</summary>
    public const string ModelChanged = "nizima.modelChanged";
}

public sealed record NizimaModel(string ModelId, string Name, string SceneId);

/// <summary>An expression or motion of a model: its name and the file path the API takes.</summary>
public sealed record NizimaClip(string Name, string Path, bool Active = false);

/// <summary>Something registered in nizima LIVE's model or item list: its name and file path.</summary>
public sealed record NizimaRegistered(string Name, string Path);

public sealed record NizimaItem(string ItemId, string Name, string SceneId);

public sealed record NizimaEffectGroup(string GroupId, string Name, bool Enabled);

/// <summary>
/// nizima LIVE through its Plugin API (github.com/Live2D/nizimaLIVEPluginAPI): WebSocket ws://localhost:22022/, messages
/// <c>{"nLPlugin":"1.0.0","Id":..,"Type":"Request|Response|Event|Error","Method":..,"Data":{..}}</c>. The first time,
/// Cuebot registers (RegisterPlugin) and keeps the token; later it sends EstablishConnection with it. The user then
/// enables Cuebot in nizima LIVE's Plugin Manager, which nizima LIVE reports with NotifyEnabledChanged; only then do
/// the other methods work.
/// </summary>
public sealed class NizimaService : StreamAppConnection
{
    public const string ApiVersion = "1.0.0";
    public const string PluginName = "Cuebot";

    private readonly JsonFile<StreamAppsSettings> _settings;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _nextId;
    private volatile bool _enabled;
    private volatile string _currentModelId = "";
    private volatile IReadOnlyList<NizimaModel> _models = [];
    private volatile IReadOnlyList<NizimaClip> _expressions = [];
    private volatile IReadOnlyList<NizimaClip> _motions = [];
    private volatile IReadOnlyList<NizimaRegistered> _registeredModels = [];
    private volatile IReadOnlyList<NizimaRegistered> _registeredItems = [];
    private volatile IReadOnlyList<NizimaItem> _items = [];
    private volatile IReadOnlyList<NizimaEffectGroup> _effectGroups = [];

    public NizimaService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), StreamAppsTimings.Default)
    {
    }

    internal NizimaService(JsonFile<StreamAppsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports, StreamAppsTimings timings)
        : base("nizima LIVE", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public NizimaSettings Settings => _settings.Value.Nizima;

    public void SaveSettings() => _settings.ScheduleSave();

    public string Host => Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "localhost";

    public int Port => Settings.Port is > 0 and < 65536 ? Settings.Port : 22022;

    /// <summary>True once the user enabled Cuebot in nizima LIVE's Plugin Manager (the API works only then).</summary>
    public bool PluginEnabled => _enabled;

    public string CurrentModelId => _currentModelId;

    public NizimaModel? CurrentModel => _models.FirstOrDefault(m => m.ModelId == _currentModelId);

    public IReadOnlyList<NizimaModel> Models => _models;

    /// <summary>The selected model's expressions and motions.</summary>
    public IReadOnlyList<NizimaClip> Expressions => _expressions;
    public IReadOnlyList<NizimaClip> Motions => _motions;
    public IReadOnlyList<NizimaRegistered> RegisteredModels => _registeredModels;
    public IReadOnlyList<NizimaRegistered> RegisteredItems => _registeredItems;
    public IReadOnlyList<NizimaItem> Items => _items;
    public IReadOnlyList<NizimaEffectGroup> EffectGroups => _effectGroups;

    /// <summary>Raised (on a background thread) when the lists were re-read or the selected model changed.</summary>
    public event Action? DataChanged;

    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool AutoReconnect => Settings.AutoReconnect;

    private string HostPart => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    private protected override (Uri Url, string Display) Endpoint() => (new Uri($"ws://{HostPart}:{Port}/"), $"nizima LIVE at {Host}:{Port}");

    private protected override Dictionary<string, object?> ConnectionArgs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["nizima.host"] = Host,
        ["nizima.port"] = Port,
    };

    private protected override void Publish(string name, Dictionary<string, object?> args) => PublishEvent($"{NizimaEvents.Prefix}.{name}", args);

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
            MarkConnected($"Connected to nizima LIVE at {Host}:{Port}");
            var enabled = await HandshakeAsync(ct).ConfigureAwait(false);
            if (enabled) await OnEnabledAsync(ct).ConfigureAwait(false);
            else ShowWaiting();
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

    /// <summary>Connects with the saved token, or registers (again) to get one. Returns whether the plug-in is enabled.</summary>
    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        if (Settings.Token.Length > 0)
        {
            try
            {
                var established = await RequestAsync("EstablishConnection", new { Name = PluginName, Token = Settings.Token }, ct, requireEnabled: false)
                    .ConfigureAwait(false);
                return _enabled = established.Bool("Enabled") ?? false;
            }
            catch (StreamAppRequestException ex) when (ex.Message.Contains("InvalidToken", StringComparison.Ordinal))
            {
                Log.Info(Name, "nizima LIVE no longer knows Cuebot's token (removed in the Plugin Manager?); registering again.");
            }
        }
        var registered = await RequestAsync("RegisterPlugin",
            new { Name = PluginName, Developer = "Tech Daddy Digital", Version = AppVersion.Current }, ct, requireEnabled: false).ConfigureAwait(false);
        Settings.Token = registered.Str("Token");
        SaveSettings();
        return _enabled = false;
    }

    private void ShowWaiting() => SetStatus(ConnectionState.Connected,
        "Connected to nizima LIVE, waiting: switch Cuebot on in nizima LIVE's Plugin Manager");

    private async Task OnEnabledAsync(CancellationToken ct)
    {
        try
        {
            // Events are off until asked for, every connection.
            await RequestAsync("NotifyCurrentModelChanged", new { Enabled = true }, ct).ConfigureAwait(false);
            await RequestAsync("NotifyModelsChanged", new { Enabled = true }, ct).ConfigureAwait(false);
            await RequestAsync("NotifyItemsChanged", new { Enabled = true }, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException)
        {
            Log.Warn(Name, $"Could not read the models: {ex.Message}");
        }
    }

    private protected override void OnSessionEnded()
    {
        _enabled = false;
        _currentModelId = "";
        _models = [];
        _expressions = [];
        _motions = [];
        _registeredModels = [];
        _registeredItems = [];
        _items = [];
        _effectGroups = [];
        DataChanged?.Invoke();
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
            {
                if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } message) continue;
                var data = message.TryGetProperty("Data", out var d) ? d.Clone() : default;
                var type = message.Str("Type");
                if (type == "Event")
                {
                    OnEvent(message.Str("Method"), data);
                    continue;
                }
                if (message.Str("Id") is { Length: > 0 } id && _pending.TryRemove(id, out var tcs))
                {
                    if (type == "Error")
                        tcs.TrySetException(new StreamAppRequestException(
                            $"nizima LIVE refused {message.Str("Method")}: {(data.ValueKind == JsonValueKind.Object ? data.Str("ErrorType") : "")}"));
                    else tcs.TrySetResult(data);
                }
            }
        }
        finally
        {
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new StreamAppNotConnectedException(Name));
        }
    }

    private void OnEvent(string method, JsonElement data)
    {
        switch (method)
        {
            case "NotifyEnabledChanged":
                var enabled = data.ValueKind == JsonValueKind.Object && (data.Bool("Enabled") ?? false);
                _enabled = enabled;
                if (enabled) _ = Task.Run(() => OnEnabledAsync(CancellationToken.None));
                else
                {
                    ShowWaiting();
                    DataChanged?.Invoke();
                }
                break;
            case "NotifyCurrentModelChanged" when data.ValueKind == JsonValueKind.Object:
                var previous = CurrentModel?.Name ?? "";
                _currentModelId = data.Str("ModelId");
                PublishEvent(NizimaEvents.ModelChanged, new()
                {
                    ["modelId"] = _currentModelId,
                    ["model"] = CurrentModel?.Name ?? "",
                    ["previousModel"] = previous,
                });
                Background(ct => RefreshModelAsync(ct));
                break;
            case "NotifyModelsChanged" when data.ValueKind == JsonValueKind.Object:
                _models = ParseModels(data);
                DataChanged?.Invoke();
                break;
            case "NotifyItemsChanged" when data.ValueKind == JsonValueKind.Object:
                _items = ParseItems(data);
                DataChanged?.Invoke();
                break;
        }
    }

    private void Background(Func<CancellationToken, Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or StreamAppRequestException or StreamAppNotConnectedException)
        {
            Log.Warn(Name, $"Could not re-read nizima LIVE: {ex.Message}");
        }
    });

    // ---- Requests ----

    /// <summary>Sends one request and waits for its Response (an Error becomes <see cref="StreamAppRequestException"/>).</summary>
    internal async Task<JsonElement> RequestAsync(string method, object? data, CancellationToken ct, bool requireEnabled = true)
    {
        EnsureConnected(requireEnabled);
        var id = $"cuebot{Interlocked.Increment(ref _nextId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(JsonUtil.Serialize(new
            {
                nLPlugin = ApiVersion,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id = id,
                Type = "Request",
                Method = method,
                Data = data ?? new { },
            }), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"nizima LIVE did not answer {method} in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void EnsureConnected(bool requireEnabled)
    {
        EnsureConnected();
        if (requireEnabled && !_enabled)
            throw new StreamAppRequestException("Cuebot is not switched on in nizima LIVE yet: open its Plugin Manager and turn Cuebot on.");
    }

    /// <summary>Re-reads models, registered models and items, effects, and the selected model's expressions and motions.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _models = ParseModels(await RequestAsync("GetModels", null, ct).ConfigureAwait(false));
            try
            {
                _currentModelId = (await RequestAsync("GetCurrentModelId", null, ct).ConfigureAwait(false)).Str("ModelId");
            }
            catch (StreamAppRequestException)
            {
                _currentModelId = _models.FirstOrDefault()?.ModelId ?? ""; // CurrentModelUnavailable: nothing selected yet.
            }
            _registeredModels = Registered(await RequestAsync("GetRegisteredModels", null, ct).ConfigureAwait(false), "RegisteredModels", "ModelPath");
            _registeredItems = Registered(await RequestAsync("GetRegisteredItems", null, ct).ConfigureAwait(false), "RegisteredItems", "ItemPath");
            _items = ParseItems(await RequestAsync("GetItems", null, ct).ConfigureAwait(false));
            var groups = await RequestAsync("GetEffectGroups", null, ct).ConfigureAwait(false);
            _effectGroups = Array(groups, "EffectGroups").Select(g => new NizimaEffectGroup(g.Str("GroupId"), g.Str("Name"), g.Bool("Enabled") ?? false)).ToList();
        }
        finally
        {
            _refreshGate.Release();
        }
        await RefreshModelAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Re-reads the selected model's expressions and motions.</summary>
    private async Task RefreshModelAsync(CancellationToken ct)
    {
        var id = _currentModelId;
        if (id.Length == 0)
        {
            _expressions = [];
            _motions = [];
        }
        else
        {
            _expressions = Array(await RequestAsync("GetExpressions", new { ModelId = id }, ct).ConfigureAwait(false), "Expressions")
                .Select(e => new NizimaClip(e.Str("Name"), e.Str("ExpressionPath"), e.Bool("Active") ?? false)).ToList();
            _motions = Array(await RequestAsync("GetMotions", new { ModelId = id }, ct).ConfigureAwait(false), "Motions")
                .Select(m => new NizimaClip(m.Str("Name"), m.Str("MotionPath"))).ToList();
        }
        if (IsConnected && _enabled)
            SetStatus(ConnectionState.Connected,
                $"Connected to nizima LIVE at {Host}:{Port}: {(CurrentModel?.Name is { Length: > 0 } name ? $"model {name}, " : "")}" +
                $"{_expressions.Count} expressions, {_motions.Count} motions");
        DataChanged?.Invoke();
    }

    private static IEnumerable<JsonElement> Array(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static List<NizimaModel> ParseModels(JsonElement data) =>
        [.. Array(data, "Models").Select(m => new NizimaModel(m.Str("ModelId"), m.Str("Name"), m.Str("SceneId")))];

    private static List<NizimaItem> ParseItems(JsonElement data) =>
        [.. Array(data, "Items").Select(i => new NizimaItem(i.Str("ItemId"), i.Str("Name"), i.Str("SceneId")))];

    private static List<NizimaRegistered> Registered(JsonElement data, string property, string pathProperty) =>
        [.. Array(data, property).Select(r => new NizimaRegistered(r.Str("Name"), r.Str(pathProperty)))];

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A model on screen by name or id; empty means the selected one.</summary>
    internal NizimaModel Model(string model)
    {
        if (model.Trim().Length == 0)
            return CurrentModel ?? _models.FirstOrDefault() ?? throw new StreamAppRequestException("nizima LIVE shows no model.");
        return _models.FirstOrDefault(m => Same(m.Name, model) || m.ModelId == model.Trim())
            ?? throw new StreamAppRequestException($"nizima LIVE shows no model called '{model.Trim()}'.");
    }

    /// <summary>The expressions or motions of a model (the selected model's are cached; others are asked for).</summary>
    private async Task<IReadOnlyList<NizimaClip>> ClipsAsync(NizimaModel model, bool expressions, CancellationToken ct)
    {
        if (model.ModelId == _currentModelId) return expressions ? _expressions : _motions;
        var data = await RequestAsync(expressions ? "GetExpressions" : "GetMotions", new { model.ModelId }, ct).ConfigureAwait(false);
        return expressions
            ? Array(data, "Expressions").Select(e => new NizimaClip(e.Str("Name"), e.Str("ExpressionPath"), e.Bool("Active") ?? false)).ToList()
            : Array(data, "Motions").Select(m => new NizimaClip(m.Str("Name"), m.Str("MotionPath"))).ToList();
    }

    private static NizimaClip Pick(IReadOnlyList<NizimaClip> clips, string name, string kind, NizimaModel model) =>
        clips.FirstOrDefault(c => Same(c.Name, name)) ?? clips.FirstOrDefault(c => Same(c.Path, name))
        ?? throw new StreamAppRequestException($"{model.Name} has no {kind} called '{name.Trim()}'.");

    /// <summary>
    /// Turns an expression on or off (Toggle uses whether nizima LIVE says it is on); "only" turns the others off first.
    /// Returns whether it is now on.
    /// </summary>
    public async Task<bool> SetExpressionAsync(string model, string expression, SwitchMode mode, bool only, CancellationToken ct = default)
    {
        var target = Model(model);
        var name = Steps.Require(expression, "Expression");
        // Fresh list: Active changes when the user clicks expressions in nizima LIVE.
        var data = await RequestAsync("GetExpressions", new { target.ModelId }, ct).ConfigureAwait(false);
        var clips = Array(data, "Expressions").Select(e => new NizimaClip(e.Str("Name"), e.Str("ExpressionPath"), e.Bool("Active") ?? false)).ToList();
        if (target.ModelId == _currentModelId) _expressions = clips;
        var clip = Pick(clips, name, "expression", target);
        var on = Steps.Desired(mode, clip.Active);
        if (on && only) await RequestAsync("StopAllExpressions", new { target.ModelId }, ct).ConfigureAwait(false);
        await RequestAsync(on ? "StartExpression" : "StopExpression", new { target.ModelId, ExpressionPath = clip.Path }, ct).ConfigureAwait(false);
        return on;
    }

    public Task StopAllExpressionsAsync(string model, CancellationToken ct = default) =>
        RequestAsync("StopAllExpressions", new { Model(model).ModelId }, ct);

    /// <summary>Plays a motion by name. Returns the motion's name.</summary>
    public async Task<string> StartMotionAsync(string model, string motion, CancellationToken ct = default)
    {
        var target = Model(model);
        var clip = Pick(await ClipsAsync(target, expressions: false, ct).ConfigureAwait(false), Steps.Require(motion, "Motion"), "motion", target);
        await RequestAsync("StartMotion", new { target.ModelId, MotionPath = clip.Path }, ct).ConfigureAwait(false);
        return clip.Name;
    }

    public Task StopMotionAsync(string model, CancellationToken ct = default) => RequestAsync("StopMotion", new { Model(model).ModelId }, ct);

    /// <summary>Presses one of the model's hotkeys, written like F1 or Ctrl+A.</summary>
    public Task TriggerHotkeyAsync(string model, string key, CancellationToken ct = default) =>
        RequestAsync("TriggerModelHotkey", new { Model(model).ModelId, Key = Steps.Require(key, "Hotkey") }, ct);

    /// <summary>Puts an item from nizima LIVE's item list on screen, next to the model. Returns the new item's id.</summary>
    public async Task<string> AddItemAsync(string model, string item, CancellationToken ct = default)
    {
        var name = Steps.Require(item, "Item");
        var registered = _registeredItems.FirstOrDefault(r => Same(r.Name, name))
            ?? throw new StreamAppRequestException($"nizima LIVE's item list has no item called '{name}'.");
        var answer = await RequestAsync("AddItem", new { Model(model).SceneId, ItemPath = registered.Path }, ct).ConfigureAwait(false);
        return answer.Str("ItemId");
    }

    /// <summary>Takes items with this name off the screen. Returns how many.</summary>
    public async Task<int> RemoveItemsAsync(string item, CancellationToken ct = default)
    {
        var name = Steps.Require(item, "Item");
        _items = ParseItems(await RequestAsync("GetItems", null, ct).ConfigureAwait(false));
        var matches = _items.Where(i => Same(i.Name, name) || i.ItemId == name).ToList();
        foreach (var match in matches) await RequestAsync("RemoveItem", new { match.ItemId }, ct).ConfigureAwait(false);
        return matches.Count;
    }

    /// <summary>Swaps a model on screen for one from nizima LIVE's model list.</summary>
    public async Task ChangeModelAsync(string model, string newModel, CancellationToken ct = default)
    {
        var name = Steps.Require(newModel, "New model");
        var registered = _registeredModels.FirstOrDefault(r => Same(r.Name, name))
            ?? throw new StreamAppRequestException($"nizima LIVE's model list has no model called '{name}'.");
        await RequestAsync("ChangeModel", new { Model(model).ModelId, ModelPath = registered.Path }, ct).ConfigureAwait(false);
    }

    /// <summary>Turns an effect group on or off (Toggle flips it). Returns whether it is now on.</summary>
    public async Task<bool> SetEffectAsync(string effect, SwitchMode mode, CancellationToken ct = default)
    {
        var name = Steps.Require(effect, "Effect");
        var groups = Array(await RequestAsync("GetEffectGroups", null, ct).ConfigureAwait(false), "EffectGroups")
            .Select(g => new NizimaEffectGroup(g.Str("GroupId"), g.Str("Name"), g.Bool("Enabled") ?? false)).ToList();
        _effectGroups = groups;
        var group = groups.FirstOrDefault(g => Same(g.Name, name)) ?? groups.FirstOrDefault(g => g.GroupId == name)
            ?? throw new StreamAppRequestException($"nizima LIVE has no effect called '{name}'.");
        var on = Steps.Desired(mode, group.Enabled);
        await RequestAsync(on ? "EnableEffectGroup" : "DisableEffectGroup", new { group.GroupId }, ct).ConfigureAwait(false);
        return on;
    }
}
