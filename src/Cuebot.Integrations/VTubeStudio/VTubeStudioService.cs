using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>
/// The VTube Studio plugin connection (public API, ws://localhost:8001): plugin authentication with a stored token
/// (requested again when VTube Studio rejects it), event subscriptions republished as bot events, cached models,
/// hotkeys, expressions, items and parameters for editor suggestions and the UI, and typed commands.
/// </summary>
public sealed class VTubeStudioService : IntegrationConnection
{
    /// <summary>Held parameter injections stop instead of waiting less than this for one more resend.</summary>
    private static readonly TimeSpan MinimumResendWait = TimeSpan.FromMilliseconds(15);

    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly PendingRequests _pending = new();
    private readonly VtsTrackingState _tracking = new();
    private long _nextId;
    private VtsCurrentModel? _currentModel;
    private VtsModel[] _models = [];
    private VtsHotkey[] _hotkeys = [];
    private VtsExpression[] _expressions = [];
    private VtsItemInstance[] _items = [];
    private VtsItemFile[] _itemFiles = [];
    private string[] _parameters = [];

    public VTubeStudioService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default)
    {
    }

    internal VTubeStudioService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, IntegrationsTimings timings)
        : base("VTube Studio", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public VTubeStudioSettings Settings => _settings.Value.VTubeStudio;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>True while VTube Studio shows the "allow plugin" popup and Cuebot waits for the click.</summary>
    public bool AwaitingApproval { get; private set; }

    /// <summary>"1.28.15" once connected.</summary>
    public string? Version { get; private set; }

    public VtsCurrentModel? CurrentModel => _currentModel;
    public IReadOnlyList<VtsModel> Models => _models;
    public IReadOnlyList<VtsHotkey> Hotkeys => _hotkeys;
    public IReadOnlyList<VtsExpression> Expressions => _expressions;
    public IReadOnlyList<VtsItemInstance> Items => _items;
    public IReadOnlyList<VtsItemFile> ItemFiles => _itemFiles;
    public IReadOnlyList<string> ParameterNames => _parameters;

    /// <summary>Raised (on a background thread) when a cached list or the current model changes.</summary>
    public event Action? DataChanged;

    /// <summary>Forgets the plugin token; the next connection asks VTube Studio for a new one.</summary>
    public void ResetAuthorization()
    {
        Settings.AuthToken = "";
        SaveSettings();
    }

    protected override string EventPrefix => VTubeStudioEvents.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [Settings.AuthToken];

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints()
    {
        var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "localhost";
        var port = Settings.Port is > 0 and < 65536 ? Settings.Port : 8001;
        return [new IntegrationEndpoint(new Uri($"ws://{host}:{port}"), $"{host}:{port}")];
    }

    // ---- Requests ----

    /// <summary>Sends a request and returns its <c>data</c>.</summary>
    /// <exception cref="IntegrationNotConnectedException">VTube Studio is not connected.</exception>
    /// <exception cref="IntegrationRequestException">VTube Studio answered with an APIError.</exception>
    public Task<JsonElement> RequestAsync(string messageType, object? data = null, CancellationToken ct = default)
    {
        EnsureConnected();
        return RequestCoreAsync(SendAsync, messageType, data, Timings.RequestTimeout, ct);
    }

    private async Task<JsonElement> RequestCoreAsync(Func<string, CancellationToken, Task> send, string messageType, object? data,
        TimeSpan timeout, CancellationToken ct)
    {
        var id = "cuebot-" + Interlocked.Increment(ref _nextId);
        var response = _pending.Add(id);
        try
        {
            await send(VtsProtocol.Request(messageType, id, data), ct).ConfigureAwait(false);
            return await response.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"VTube Studio did not answer {messageType} in time.");
        }
        finally
        {
            _pending.Remove(id);
        }
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            Task Send(string text, CancellationToken token) => transport.SendAsync(text, token);
            var state = await RequestCoreAsync(Send, "APIStateRequest", null, Timings.RequestTimeout, ct).ConfigureAwait(false);
            Version = state.Str("vTubeStudioVersion");
            await AuthenticateAsync(Send, ct).ConfigureAwait(false);
            MarkConnected($"Connected to VTube Studio{(Version.Length > 0 ? " " + Version : "")} at {endpoint.Display}",
                new Dictionary<string, object?> { ["version"] = Version });
            await SubscribeAsync(ct).ConfigureAwait(false);
            try
            {
                await RefreshAllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IntegrationRequestException or TimeoutException)
            {
                Log.Debug(Name, $"Initial refresh incomplete: {ex.Message}");
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

    private async Task AuthenticateAsync(Func<string, CancellationToken, Task> send, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = Settings.AuthToken;
            if (token.Length == 0) token = await RequestTokenAsync(send, ct).ConfigureAwait(false);
            var auth = await RequestCoreAsync(send, "AuthenticationRequest",
                new { pluginName = PluginName, pluginDeveloper = PluginDeveloper, authenticationToken = token },
                Timings.RequestTimeout, ct).ConfigureAwait(false);
            if (auth.Bool("authenticated") == true) return;
            Log.Info(Name, $"VTube Studio rejected the saved plugin token ({auth.Str("reason")}); asking for a new one.");
            Settings.AuthToken = "";
            SaveSettings();
        }
        throw new IntegrationAuthException("VTube Studio did not accept the plugin token. Click Connect to try again.");
    }

    private async Task<string> RequestTokenAsync(Func<string, CancellationToken, Task> send, CancellationToken ct)
    {
        AwaitingApproval = true;
        SetStatus(ConnectionState.Connecting, "Waiting for approval: click \"Allow\" on the Cuebot plugin popup in VTube Studio.");
        try
        {
            var data = await RequestCoreAsync(send, "AuthenticationTokenRequest",
                new { pluginName = PluginName, pluginDeveloper = PluginDeveloper }, Timings.AuthApprovalTimeout, ct).ConfigureAwait(false);
            var token = data.Str("authenticationToken");
            if (token.Length == 0) throw new IntegrationAuthException("VTube Studio sent no plugin token.");
            Settings.AuthToken = token;
            SaveSettings();
            Log.Info(Name, "VTube Studio approved the Cuebot plugin; token saved.");
            return token;
        }
        catch (IntegrationRequestException ex) when (ex.ErrorId == VtsProtocol.ErrorTokenRequestDenied)
        {
            throw new IntegrationAuthException("The plugin request was denied in VTube Studio. Click Connect to ask again.");
        }
        catch (TimeoutException)
        {
            throw new IntegrationAuthException("Nobody allowed the Cuebot plugin in VTube Studio. Click Connect to ask again.");
        }
        finally
        {
            AwaitingApproval = false;
        }
    }

    private string PluginName => Clamp(Settings.PluginName, "Cuebot");
    private string PluginDeveloper => Clamp(Settings.PluginDeveloper, "Cuebot contributors");

    private static string Clamp(string value, string fallback)
    {
        value = value.Trim();
        if (value.Length < 3) value = fallback;
        return value.Length > 32 ? value[..32] : value;
    }

    private async Task SubscribeAsync(CancellationToken ct)
    {
        foreach (var eventName in VtsProtocol.SubscribedEvents)
        {
            try
            {
                await RequestAsync("EventSubscriptionRequest", new { eventName, subscribe = true, config = new { } }, ct).ConfigureAwait(false);
            }
            catch (IntegrationRequestException ex)
            {
                // Older VTube Studio versions lack some events (e.g. beta ExpressionToggledEvent).
                Log.Debug(Name, $"Could not subscribe to {eventName}: {ex.Message}");
            }
        }
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) Handle(text);
        }
        finally
        {
            _pending.FailAll(new IntegrationNotConnectedException(Name));
        }
    }

    private void Handle(string text)
    {
        if (VtsProtocol.Parse(text) is not { } message) return;
        if (message.IsEvent)
        {
            HandleEvent(message);
            return;
        }
        if (message.IsError) _pending.Fail(message.RequestId, null, VtsProtocol.Error(message));
        else _pending.Complete(message.RequestId, null, message.Data);
    }

    private void HandleEvent(VtsMessage message)
    {
        foreach (var (type, args) in VtsEventMapper.Map(message.MessageType, message.Data, _tracking))
            Publish(type[(VTubeStudioEvents.Prefix.Length + 1)..], args);
        switch (message.MessageType)
        {
            case "ModelLoadedEvent" or "ModelConfigChangedEvent":
                RefreshInBackground(RefreshModelAsync);
                break;
            case "ModelMovedEvent":
                if (_currentModel is { } model)
                {
                    var p = message.Data.Obj("modelPosition");
                    _currentModel = model with
                    {
                        Position = new VtsModelPosition(p.Num("positionX") ?? 0, p.Num("positionY") ?? 0, p.Num("rotation") ?? 0, p.Num("size") ?? 0),
                    };
                    DataChanged?.Invoke();
                }
                break;
            case "ItemEvent" when message.Data.Str("itemEventType") is "Added" or "Removed":
                RefreshInBackground(RefreshItemsAsync);
                break;
            case "ExpressionToggledEvent":
                RefreshInBackground(RefreshExpressionsAsync);
                break;
        }
    }

    /// <summary>Never awaited on the receive loop (its responses arrive on that loop).</summary>
    private void RefreshInBackground(Func<CancellationToken, Task> refresh) => _ = Task.Run(async () =>
    {
        try
        {
            await refresh(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IntegrationNotConnectedException or IntegrationRequestException or TimeoutException)
        {
            Log.Debug(Name, $"Refresh failed: {ex.Message}");
        }
    });

    private protected override void OnSessionEnded()
    {
        _currentModel = null;
        _tracking.FaceFound = null;
        AwaitingApproval = false;
        DataChanged?.Invoke();
    }

    // ---- Cached data ----

    /// <summary>Refreshes the current model, models, hotkeys, expressions, items and parameters.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshModelAsync(ct).ConfigureAwait(false);
        await RefreshItemsAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshModelAsync(CancellationToken ct)
    {
        await GetCurrentModelAsync(ct).ConfigureAwait(false);
        await GetModelsAsync(ct).ConfigureAwait(false);
        try
        {
            await GetHotkeysAsync(ct).ConfigureAwait(false);
        }
        catch (IntegrationRequestException)
        {
            _hotkeys = []; // No model loaded.
        }
        await RefreshExpressionsAsync(ct).ConfigureAwait(false);
        try
        {
            _parameters = VtsProtocol.ParseParameterNames(await RequestAsync("InputParameterListRequest", null, ct).ConfigureAwait(false));
        }
        catch (IntegrationRequestException)
        {
            // No model loaded.
        }
        DataChanged?.Invoke();
    }

    private async Task RefreshExpressionsAsync(CancellationToken ct)
    {
        await GetExpressionsAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshItemsAsync(CancellationToken ct)
    {
        var data = await RequestAsync("ItemListRequest", new
        {
            includeAvailableSpots = false,
            includeItemInstancesInScene = true,
            includeAvailableItemFiles = true,
        }, ct).ConfigureAwait(false);
        _items = VtsProtocol.ParseItemInstances(data);
        _itemFiles = VtsProtocol.ParseItemFiles(data);
        DataChanged?.Invoke();
    }

    public async Task<VtsCurrentModel> GetCurrentModelAsync(CancellationToken ct = default)
    {
        var model = VtsProtocol.ParseCurrentModel(await RequestAsync("CurrentModelRequest", null, ct).ConfigureAwait(false));
        _currentModel = model;
        DataChanged?.Invoke();
        return model;
    }

    public async Task<IReadOnlyList<VtsModel>> GetModelsAsync(CancellationToken ct = default)
    {
        _models = VtsProtocol.ParseModels(await RequestAsync("AvailableModelsRequest", null, ct).ConfigureAwait(false));
        DataChanged?.Invoke();
        return _models;
    }

    public async Task<IReadOnlyList<VtsHotkey>> GetHotkeysAsync(CancellationToken ct = default)
    {
        _hotkeys = VtsProtocol.ParseHotkeys(await RequestAsync("HotkeysInCurrentModelRequest", null, ct).ConfigureAwait(false));
        DataChanged?.Invoke();
        return _hotkeys;
    }

    public async Task<IReadOnlyList<VtsExpression>> GetExpressionsAsync(CancellationToken ct = default)
    {
        try
        {
            _expressions = VtsProtocol.ParseExpressions(await RequestAsync("ExpressionStateRequest", new { details = false }, ct).ConfigureAwait(false));
        }
        catch (IntegrationRequestException)
        {
            _expressions = []; // No model loaded.
        }
        DataChanged?.Invoke();
        return _expressions;
    }

    // ---- Commands ----

    /// <summary>Triggers a hotkey by name or id; returns the id VTube Studio reports.</summary>
    public async Task<string> TriggerHotkeyAsync(string nameOrId, string? itemInstanceId = null, CancellationToken ct = default)
    {
        nameOrId = Steps.Require(nameOrId, "Hotkey");
        var hotkey = FindHotkey(nameOrId);
        if (hotkey is null && IsConnected)
        {
            await GetHotkeysAsync(ct).ConfigureAwait(false);
            hotkey = FindHotkey(nameOrId);
        }
        var data = await RequestAsync("HotkeyTriggerRequest", new
        {
            hotkeyID = hotkey?.Id ?? nameOrId, // VTube Studio also accepts a hotkey name here
            itemInstanceID = string.IsNullOrWhiteSpace(itemInstanceId) ? null : itemInstanceId.Trim(),
        }, ct).ConfigureAwait(false);
        return data.Str("hotkeyID");
    }

    private VtsHotkey? FindHotkey(string nameOrId) =>
        _hotkeys.FirstOrDefault(h => h.Id == nameOrId)
        ?? _hotkeys.FirstOrDefault(h => string.Equals(h.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads a model by name or id; returns its id.</summary>
    public async Task<string> LoadModelAsync(string nameOrId, CancellationToken ct = default)
    {
        nameOrId = Steps.Require(nameOrId, "Model");
        var models = _models.Length > 0 ? _models : (await GetModelsAsync(ct).ConfigureAwait(false)).ToArray();
        var model = Find(models) ?? Find((await GetModelsAsync(ct).ConfigureAwait(false)).ToArray())
            ?? throw new ArgumentException($"VTube Studio has no model named '{nameOrId}'.");
        await RequestAsync("ModelLoadRequest", new { modelID = model.Id }, ct).ConfigureAwait(false);
        return model.Id;

        VtsModel? Find(VtsModel[] list) =>
            list.FirstOrDefault(m => m.Id == nameOrId) ?? list.FirstOrDefault(m => string.Equals(m.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Moves the model; null values stay unchanged.</summary>
    public Task MoveModelAsync(double timeInSeconds, bool relative, double? positionX, double? positionY, double? rotation, double? size,
        CancellationToken ct = default) =>
        RequestAsync("MoveModelRequest", new
        {
            timeInSeconds = Math.Clamp(timeInSeconds, 0, 2),
            valuesAreRelativeToModel = relative,
            positionX,
            positionY,
            rotation,
            size,
        }, ct);

    /// <summary>Activates, deactivates or toggles an expression (by name or file); returns the new state.</summary>
    public async Task<bool> SetExpressionAsync(string nameOrFile, SwitchMode mode, double fadeTime = 0.25, CancellationToken ct = default)
    {
        nameOrFile = Steps.Require(nameOrFile, "Expression");
        var expressions = await GetExpressionsAsync(ct).ConfigureAwait(false);
        var expression = expressions.FirstOrDefault(e => string.Equals(e.File, nameOrFile, StringComparison.OrdinalIgnoreCase))
            ?? expressions.FirstOrDefault(e => string.Equals(e.DisplayName, nameOrFile, StringComparison.OrdinalIgnoreCase));
        var file = expression?.File ?? (nameOrFile.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) ? nameOrFile : nameOrFile + ".exp3.json");
        var active = Steps.Desired(mode, expression?.Active ?? false);
        await RequestAsync("ExpressionActivationRequest", new { expressionFile = file, fadeTime = Math.Clamp(fadeTime, 0, 2), active }, ct)
            .ConfigureAwait(false);
        return active;
    }

    /// <summary>Deactivates every active expression; returns how many.</summary>
    public async Task<int> RemoveAllExpressionsAsync(double fadeTime = 0.25, CancellationToken ct = default)
    {
        var active = (await GetExpressionsAsync(ct).ConfigureAwait(false)).Where(e => e.Active).ToArray();
        foreach (var e in active)
            await RequestAsync("ExpressionActivationRequest", new { expressionFile = e.File, fadeTime = Math.Clamp(fadeTime, 0, 2), active = false }, ct)
                .ConfigureAwait(false);
        return active.Length;
    }

    /// <summary>
    /// Injects tracking parameter values. VTube Studio keeps injected values for about a second, so with
    /// <paramref name="hold"/> the values are re-sent every <see cref="IntegrationsTimings.InjectInterval"/> until it elapses.
    /// </summary>
    public async Task InjectParametersAsync(IReadOnlyList<VtsParameterValue> values, bool add, bool faceFound, TimeSpan hold,
        CancellationToken ct = default)
    {
        var request = new
        {
            faceFound,
            mode = add ? "add" : "set",
            parameterValues = values.Select(v => new { id = v.Id, value = v.Value, weight = v.Weight }).ToArray(),
        };
        var until = DateTime.UtcNow + hold;
        while (true)
        {
            await RequestAsync("InjectParameterDataRequest", request, ct).ConfigureAwait(false);
            var left = until - DateTime.UtcNow;
            // VTube Studio keeps the last values for about a second, so a remainder this short needs no resend; it would
            // also make Task.Delay return at once and spin out a burst of requests.
            if (left < MinimumResendWait) return;
            await Task.Delay(left < Timings.InjectInterval ? left : Timings.InjectInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Tints art meshes. With no name/tag filters every mesh is tinted.</summary>
    public async Task<int> ColorTintAsync((int R, int G, int B, int A) color, IReadOnlyList<string> nameContains,
        IReadOnlyList<string> tagContains, double mixWithSceneLighting = 1, CancellationToken ct = default)
    {
        var all = nameContains.Count == 0 && tagContains.Count == 0;
        var data = await RequestAsync("ColorTintRequest", new
        {
            colorTint = new { colorR = color.R, colorG = color.G, colorB = color.B, colorA = color.A, mixWithSceneLightingColor = Math.Clamp(mixWithSceneLighting, 0, 1) },
            artMeshMatcher = new
            {
                tintAll = all,
                nameContains = all ? null : nameContains.ToArray(),
                tagContains = all ? null : tagContains.ToArray(),
            },
        }, ct).ConfigureAwait(false);
        return (int)(data.Num("matchedArtMeshes") ?? 0);
    }

    public Task<int> ResetColorTintAsync(CancellationToken ct = default) => ColorTintAsync((255, 255, 255, 255), [], [], 1, ct);

    /// <summary>Loads an item into the scene; returns its instance id.</summary>
    public async Task<string> LoadItemAsync(string fileName, double positionX, double positionY, double size, double rotation,
        double fadeTime, int? order, bool unloadWhenPluginDisconnects, CancellationToken ct = default)
    {
        var data = await RequestAsync("ItemLoadRequest", new
        {
            fileName = Steps.Require(fileName, "Item file"),
            positionX,
            positionY,
            size,
            rotation,
            fadeTime = Math.Clamp(fadeTime, 0, 2),
            order,
            failIfOrderTaken = false,
            smoothing = 0,
            censored = false,
            flipped = false,
            locked = false,
            unloadWhenPluginDisconnects,
        }, ct).ConfigureAwait(false);
        return data.Str("instanceID");
    }

    /// <summary>Unloads items by instance id and/or file name, or every item in the scene; returns how many.</summary>
    public async Task<int> UnloadItemsAsync(IReadOnlyList<string> instanceIds, IReadOnlyList<string> fileNames, bool all,
        CancellationToken ct = default)
    {
        if (!all && instanceIds.Count == 0 && fileNames.Count == 0) throw new ArgumentException("Enter an item instance id or file name.");
        var data = await RequestAsync("ItemUnloadRequest", new
        {
            unloadAllInScene = all,
            unloadAllLoadedByThisPlugin = false,
            allowUnloadingItemsLoadedByUserOrOtherPlugins = true,
            instanceIDs = instanceIds.ToArray(),
            fileNames = fileNames.ToArray(),
        }, ct).ConfigureAwait(false);
        return data.Array("unloadedItems").Count();
    }

    /// <summary>Moves an item (by instance id, or the first instance of a file name); null values stay unchanged.</summary>
    public async Task MoveItemAsync(string instanceIdOrFile, double timeInSeconds, string fadeMode, double? positionX, double? positionY,
        double? size, double? rotation, CancellationToken ct = default)
    {
        instanceIdOrFile = Steps.Require(instanceIdOrFile, "Item");
        var id = _items.FirstOrDefault(i => i.InstanceId == instanceIdOrFile)?.InstanceId;
        if (id is null)
        {
            await RefreshItemsAsync(ct).ConfigureAwait(false);
            id = _items.FirstOrDefault(i => i.InstanceId == instanceIdOrFile)?.InstanceId
                ?? _items.FirstOrDefault(i => string.Equals(i.FileName, instanceIdOrFile, StringComparison.OrdinalIgnoreCase))?.InstanceId
                ?? instanceIdOrFile;
        }
        var data = await RequestAsync("ItemMoveRequest", new
        {
            itemsToMove = new[]
            {
                new
                {
                    itemInstanceID = id,
                    timeInSeconds = Math.Clamp(timeInSeconds, 0, 30),
                    fadeMode = fadeMode.Trim().Length > 0 ? fadeMode.Trim() : "linear",
                    positionX,
                    positionY,
                    size,
                    rotation,
                    userCanStop = true,
                },
            },
        }, ct).ConfigureAwait(false);
        if (data.Array("movedItems").FirstOrDefault() is { ValueKind: JsonValueKind.Object } moved && moved.Bool("success") == false)
            throw new IntegrationRequestException($"VTube Studio could not move item {id} (error {moved.Str("errorID")}).");
    }
}
