using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>
/// The VTube Studio plugin connection (public API, ws://localhost:8001): plugin authentication with a stored token
/// (requested again when VTube Studio rejects or withdraws it), event subscriptions republished as bot events, cached
/// models, hotkeys, expressions, items, ArtMeshes, physics groups, visual effects presets and parameters for editor
/// suggestions and the UI, and typed commands (this file: connection, caches, the original commands; the
/// VTubeStudioService.*.cs files: the rest).
/// </summary>
public sealed partial class VTubeStudioService : IntegrationConnection
{
    /// <summary>Held parameter injections stop instead of waiting less than this for one more resend.</summary>
    private static readonly TimeSpan MinimumResendWait = TimeSpan.FromMilliseconds(15);

    /// <summary>Instance-list refreshes after item events are merged within this window (a volley of items is one refresh).</summary>
    private static readonly TimeSpan ItemRefreshDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>The UI is told about model moves at most this often (VTube Studio reports every frame while it moves).</summary>
    private static readonly TimeSpan MoveNotifyInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Remembered tints re-applied after a reconnect (VTube Studio resets a session's tints when it ends).</summary>
    private const int MaxRememberedTints = 50;

    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly PendingRequests _pending = new();
    private readonly VtsTrackingState _tracking = new();
    private readonly IVtsDiscovery? _discovery;
    private readonly TimeProvider _time;
    private readonly VtsReverts _reverts;
    private readonly List<object> _tints = [];
    private readonly ConcurrentDictionary<string, object> _parameterOwners = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;
    private VtsCurrentModel? _currentModel;
    private VtsModel[] _models = [];
    private VtsHotkey[] _hotkeys = [];
    private VtsExpression[] _expressions = [];
    private VtsItemInstance[] _items = [];
    private VtsItemFile[] _itemFiles = [];
    private string[] _parameters = [];
    private CancellationTokenSource? _session;
    private volatile bool _authenticated;
    private volatile bool _accessWithdrawn;
    private long _lastMoveNotify;
    private int _itemRefreshQueued;
    private int _expressionRefreshQueued;

    public VTubeStudioService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default,
            new VtsUdpDiscovery(log, TimeProvider.System), TimeProvider.System)
    {
    }

    internal VTubeStudioService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, IntegrationsTimings timings, IVtsDiscovery? discovery = null, TimeProvider? time = null)
        : base("VTube Studio", bus, log, transports, timings)
    {
        _settings = settings;
        _discovery = discovery;
        _time = time ?? TimeProvider.System;
        _reverts = new VtsReverts(log, _time);
        _parameterHolder = new VtsParameterHolder(this);
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

    /// <summary>VTube Studio instances heard on the network (port and window title), for the settings page.</summary>
    public IReadOnlyList<VtsInstance> DiscoveredInstances => _discovery?.Instances ?? [];

    /// <summary>Events this VTube Studio version does not offer (for example beta-only ones), so their triggers never run.</summary>
    public IReadOnlyCollection<string> UnavailableEvents => _unavailableEvents.Keys.ToArray();

    private readonly ConcurrentDictionary<string, byte> _unavailableEvents = new(StringComparer.Ordinal);

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
        var endpoints = new List<IntegrationEndpoint> { new(new Uri($"ws://{host}:{port}"), $"{host}:{port}") };
        if (Settings.AutoDiscover && IsLocal(host) && _discovery is not null)
        {
            _discovery.Start();
            // The configured port first; then the other instances VTube Studio announced (a second window takes 8002, ...).
            foreach (var instance in _discovery.Instances.Where(i => i.Active && i.Port != port))
                endpoints.Add(new IntegrationEndpoint(new Uri($"ws://{host}:{instance.Port}"),
                    $"{host}:{instance.Port}{(instance.WindowTitle.Length > 0 ? $" ({instance.WindowTitle})" : "")}"));
        }
        return endpoints;
    }

    private static bool IsLocal(string host) => host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private protected override void OnDisposed()
    {
        _reverts.CancelAll();
        _discovery?.Dispose();
    }

    // ---- Requests ----

    /// <summary>Sends a request and returns its <c>data</c>.</summary>
    /// <exception cref="IntegrationNotConnectedException">VTube Studio is not connected.</exception>
    /// <exception cref="IntegrationRequestException">VTube Studio answered with an APIError.</exception>
    public Task<JsonElement> RequestAsync(string messageType, object? data = null, CancellationToken ct = default)
    {
        EnsureConnected();
        // Some requests only answer once the streamer clicked something in VTube Studio.
        var timeout = VtsProtocol.PopupRequests.Contains(messageType) ? Timings.AuthApprovalTimeout : Timings.RequestTimeout;
        return RequestCoreAsync(SendAsync, messageType, data, timeout, ct);
    }

    /// <summary>A request that may wait for the streamer (a popup in VTube Studio).</summary>
    private Task<JsonElement> RequestWithPopupAsync(string messageType, object? data, CancellationToken ct)
    {
        EnsureConnected();
        return RequestCoreAsync(SendAsync, messageType, data, Timings.AuthApprovalTimeout, ct);
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

    /// <summary>Retries a request that VTube Studio refused because it was busy (hotkey queue/cooldown, model cooldown).</summary>
    private async Task<JsonElement> RequestWithRetryAsync(string messageType, object data, int[] busyErrors, TimeSpan wait, int attempts,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RequestAsync(messageType, data, ct).ConfigureAwait(false);
            }
            catch (IntegrationRequestException ex) when (attempt < attempts && busyErrors.Contains(ex.ErrorId))
            {
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _session = session;
        _authenticated = false;
        _accessWithdrawn = false;
        var pump = PumpAsync(transport, session.Token);
        try
        {
            Task Send(string text, CancellationToken token) => transport.SendAsync(text, token);
            var state = await RequestCoreAsync(Send, "APIStateRequest", null, Timings.RequestTimeout, ct).ConfigureAwait(false);
            Version = state.Str("vTubeStudioVersion");
            await AuthenticateAsync(Send, ct).ConfigureAwait(false);
            _authenticated = true;
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
            await RestoreSessionStateAsync(ct).ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_accessWithdrawn && !ct.IsCancellationRequested)
            {
                // The streamer revoked Cuebot (or switched the API off): end this session so the next one asks again.
                throw new IOException("VTube Studio withdrew Cuebot's access; asking again (click \"Allow\" in VTube Studio).");
            }
        }
        finally
        {
            _authenticated = false;
            _session = null;
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

    /// <summary>After connecting: custom parameters and tints this plugin set before are gone in VTube Studio, so set them again.</summary>
    private async Task RestoreSessionStateAsync(CancellationToken ct)
    {
        foreach (var parameter in Settings.CustomParameters.ToArray())
        {
            try
            {
                await CreateParameterCoreAsync(parameter, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IntegrationRequestException or TimeoutException)
            {
                Log.Warn(Name, $"Could not create custom parameter {parameter.Name}: {ex.Message}");
            }
        }
        object[] tints;
        lock (_tints) tints = [.. _tints];
        foreach (var tint in tints)
        {
            try
            {
                await RequestAsync("ColorTintRequest", tint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IntegrationRequestException or TimeoutException)
            {
                Log.Debug(Name, $"Could not re-apply a color tint: {ex.Message}");
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
                new { pluginName = PluginName, pluginDeveloper = PluginDeveloper, pluginIcon = PluginIcon.Value },
                Timings.AuthApprovalTimeout, ct).ConfigureAwait(false);
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

    /// <summary>The Cuebot logo (128x128 PNG, base64) shown on VTube Studio's "allow plugin" popup; null if missing.</summary>
    private static readonly Lazy<string?> PluginIcon = new(() =>
    {
        using var stream = typeof(VTubeStudioService).Assembly.GetManifestResourceStream("Cuebot.Integrations.VTubeStudio.plugin-icon.png");
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Convert.ToBase64String(buffer.ToArray());
    });

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
        _unavailableEvents.Clear();
        foreach (var eventName in VtsProtocol.SubscribedEvents)
        {
            try
            {
                await RequestAsync("EventSubscriptionRequest", new { eventName, subscribe = true, config = VtsProtocol.EventConfig(eventName) }, ct)
                    .ConfigureAwait(false);
            }
            catch (IntegrationRequestException ex)
            {
                _unavailableEvents[eventName] = 0;
                if (VtsProtocol.BetaEvents.Contains(eventName))
                    Log.Info(Name, $"VTube Studio {Version} does not offer {eventName} (it is in the VTube Studio beta); its trigger will not run.");
                else
                    Log.Info(Name, $"Could not subscribe to {eventName}: {ex.Message}");
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
        if (message.IsError)
        {
            var error = VtsProtocol.Error(message);
            _pending.Fail(message.RequestId, null, error);
            if (_authenticated && error.ErrorId is VtsProtocol.ErrorRequiresAuthentication or VtsProtocol.ErrorTokenRequestDenied
                or VtsProtocol.ErrorApiAccessDeactivated)
                WithdrawAccess(error.ErrorId == VtsProtocol.ErrorApiAccessDeactivated
                    ? "The VTube Studio API was switched off."
                    : "VTube Studio no longer allows Cuebot (the plugin was revoked).");
        }
        else
        {
            _pending.Complete(message.RequestId, null, message.Data);
        }
    }

    private void WithdrawAccess(string reason)
    {
        if (_accessWithdrawn) return;
        _accessWithdrawn = true;
        Log.Warn(Name, reason);
        try
        {
            _session?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Session already over.
        }
    }

    private void HandleEvent(VtsMessage message)
    {
        if (message.MessageType == "ModelOutlineEvent")
        {
            OnOutline(message.Data);
            return; // 15 times a second while subscribed: never republished
        }
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
                    // Reported every frame while the model moves: tell the UI a few times a second at most.
                    var now = _time.GetTimestamp();
                    var last = Interlocked.Read(ref _lastMoveNotify);
                    if (_time.GetElapsedTime(last, now) >= MoveNotifyInterval && Interlocked.CompareExchange(ref _lastMoveNotify, now, last) == last)
                        DataChanged?.Invoke();
                }
                break;
            case "ItemEvent" when message.Data.Str("itemEventType") is "Added" or "Removed" or "DroppedPinned" or "DroppedUnpinned":
                QueueItemRefresh();
                break;
            case "ExpressionToggledEvent":
                // A model load sends one event per expression (justLoaded): one refresh for the whole snapshot.
                QueueExpressionRefresh();
                break;
            case "ModelClickedEvent":
                if (VtsProtocol.ParseHits(message.Data) is { Length: > 0 } hits) LastClick = hits[0];
                break;
        }
    }

    /// <summary>One instance-list refresh shortly after item events; events arriving meanwhile join it (a volley is one refresh).</summary>
    private void QueueItemRefresh()
    {
        if (Interlocked.Exchange(ref _itemRefreshQueued, 1) == 1) return;
        RefreshInBackground(async ct =>
        {
            await Task.Delay(ItemRefreshDebounce, _time, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _itemRefreshQueued, 0); // an event during the refresh queues the next one
            await RefreshItemsAsync(includeFiles: false, ct).ConfigureAwait(false);
        });
    }

    /// <summary>One expression refresh for a burst of ExpressionToggledEvents (a model load sends one per expression).</summary>
    private void QueueExpressionRefresh()
    {
        if (Interlocked.Exchange(ref _expressionRefreshQueued, 1) == 1) return;
        RefreshInBackground(async ct =>
        {
            await Task.Delay(ItemRefreshDebounce, _time, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _expressionRefreshQueued, 0);
            await RefreshExpressionsAsync(ct).ConfigureAwait(false);
        });
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
        _tracking.Reset();
        AwaitingApproval = false;
        _authenticated = false;
        _reverts.CancelAll();
        _parameterHolder.Clear();
        _parameterOwners.Clear();
        _permissions.Clear();
        LastClick = null;
        DataChanged?.Invoke();
    }

    // ---- Cached data ----

    /// <summary>Refreshes the current model, models, hotkeys, expressions, items (with the item files) and parameters.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await RefreshModelAsync(ct).ConfigureAwait(false);
        await RefreshItemsAsync(includeFiles: true, ct).ConfigureAwait(false);
        await RefreshEffectsAsync(ct).ConfigureAwait(false);
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
        await RefreshArtMeshesAsync(ct).ConfigureAwait(false);
        await RefreshPhysicsAsync(ct).ConfigureAwait(false);
        DataChanged?.Invoke();
    }

    private async Task RefreshExpressionsAsync(CancellationToken ct)
    {
        await GetExpressionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the items in the scene. <paramref name="includeFiles"/> also re-reads the item files, which makes
    /// VTube Studio read its Items folder from disk (and can make it stutter), so only on connect and on request.
    /// </summary>
    private async Task RefreshItemsAsync(bool includeFiles, CancellationToken ct)
    {
        var data = await RequestAsync("ItemListRequest", new
        {
            includeAvailableSpots = false,
            includeItemInstancesInScene = true,
            includeAvailableItemFiles = includeFiles,
        }, ct).ConfigureAwait(false);
        _items = VtsProtocol.ParseItemInstances(data);
        if (includeFiles) _itemFiles = VtsProtocol.ParseItemFiles(data);
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

    /// <summary>
    /// The hotkeys of the current model, or of another model (<paramref name="modelNameOrId"/>) or of a Live2D item
    /// (<paramref name="live2DItemFileName"/>); only the current model's are cached for suggestions.
    /// </summary>
    public async Task<IReadOnlyList<VtsHotkey>> GetHotkeysAsync(CancellationToken ct = default, string? modelNameOrId = null,
        string? live2DItemFileName = null)
    {
        string? modelId = null;
        if (!string.IsNullOrWhiteSpace(modelNameOrId))
        {
            var models = _models.Length > 0 ? _models : (await GetModelsAsync(ct).ConfigureAwait(false)).ToArray();
            modelId = models.FirstOrDefault(m => m.Id == modelNameOrId.Trim()
                || string.Equals(m.Name, modelNameOrId.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? modelNameOrId.Trim();
        }
        var item = string.IsNullOrWhiteSpace(live2DItemFileName) ? null : live2DItemFileName.Trim();
        var hotkeys = VtsProtocol.ParseHotkeys(await RequestAsync("HotkeysInCurrentModelRequest",
            modelId is null && item is null ? null : new { modelID = modelId, live2DItemFileName = item }, ct).ConfigureAwait(false));
        if (modelId is null && item is null)
        {
            _hotkeys = hotkeys;
            DataChanged?.Invoke();
        }
        return hotkeys;
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

    /// <summary>Triggers a hotkey by name or id; returns the id VTube Studio reports. Retries briefly while VTube Studio is busy.</summary>
    public async Task<string> TriggerHotkeyAsync(string nameOrId, string? itemInstanceId = null, CancellationToken ct = default)
    {
        nameOrId = Steps.Require(nameOrId, "Hotkey");
        var hotkey = FindHotkey(nameOrId);
        if (hotkey is null && IsConnected && string.IsNullOrWhiteSpace(itemInstanceId))
        {
            await GetHotkeysAsync(ct).ConfigureAwait(false);
            hotkey = FindHotkey(nameOrId);
        }
        var data = await RequestWithRetryAsync("HotkeyTriggerRequest", new
        {
            hotkeyID = hotkey?.Id ?? nameOrId, // VTube Studio also accepts a hotkey name here
            itemInstanceID = string.IsNullOrWhiteSpace(itemInstanceId) ? null : itemInstanceId.Trim(),
        }, [VtsProtocol.ErrorHotkeyQueueFull, VtsProtocol.ErrorHotkeyCooldown], TimeSpan.FromMilliseconds(500), 3, ct).ConfigureAwait(false);
        return data.Str("hotkeyID");
    }

    private VtsHotkey? FindHotkey(string nameOrId) =>
        _hotkeys.FirstOrDefault(h => h.Id == nameOrId)
        ?? _hotkeys.FirstOrDefault(h => string.Equals(h.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads a model by name or id; returns its id. With <paramref name="revertAfter"/> the previous model comes back
    /// after that time.
    /// </summary>
    public async Task<string> LoadModelAsync(string nameOrId, CancellationToken ct = default, TimeSpan? revertAfter = null)
    {
        nameOrId = Steps.Require(nameOrId, "Model");
        var models = _models.Length > 0 ? _models : (await GetModelsAsync(ct).ConfigureAwait(false)).ToArray();
        var model = Find(models) ?? Find((await GetModelsAsync(ct).ConfigureAwait(false)).ToArray())
            ?? throw new ArgumentException($"VTube Studio has no model named '{nameOrId}'.");
        await LoadModelByIdAsync(model.Id, revertAfter, ct).ConfigureAwait(false);
        return model.Id;

        VtsModel? Find(VtsModel[] list) =>
            list.FirstOrDefault(m => m.Id == nameOrId) ?? list.FirstOrDefault(m => string.Equals(m.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Loads a model by id (an empty id unloads the model), waiting out VTube Studio's 2-second model cooldown.</summary>
    private async Task LoadModelByIdAsync(string modelId, TimeSpan? revertAfter, CancellationToken ct)
    {
        string? previous = null;
        if (revertAfter is not null && !_reverts.IsPending(RevertModel))
            previous = (_currentModel ?? await GetCurrentModelAsync(ct).ConfigureAwait(false)) is { Loaded: true } current ? current.Id : "";
        await RequestWithRetryAsync("ModelLoadRequest", new { modelID = modelId }, [VtsProtocol.ErrorModelLoadCooldown],
            TimeSpan.FromSeconds(2.1), 2, ct).ConfigureAwait(false);
        if (revertAfter is { } after)
            _reverts.Schedule(RevertModel, after, token => LoadModelByIdAsync(previous ?? "", null, token));
        else
            _reverts.Cancel(RevertModel);
    }

    /// <summary>Moves the model; null values stay unchanged. With <paramref name="revertAfter"/> it moves back after that time.</summary>
    public async Task MoveModelAsync(double timeInSeconds, bool relative, double? positionX, double? positionY, double? rotation, double? size,
        CancellationToken ct = default, TimeSpan? revertAfter = null)
    {
        VtsModelPosition? before = null;
        if (revertAfter is not null && !_reverts.IsPending(RevertMove))
            before = (await GetCurrentModelAsync(ct).ConfigureAwait(false)).Position;
        await RequestAsync("MoveModelRequest", new
        {
            timeInSeconds = Math.Clamp(timeInSeconds, 0, 2),
            valuesAreRelativeToModel = relative,
            positionX,
            positionY,
            rotation,
            size,
        }, ct).ConfigureAwait(false);
        if (revertAfter is { } after)
            _reverts.Schedule(RevertMove, after, token => before is null ? Task.CompletedTask
                : MoveModelAsync(Math.Clamp(timeInSeconds, 0, 2), false, before.PositionX, before.PositionY, before.Rotation, before.Size, token));
        else
            _reverts.Cancel(RevertMove);
    }

    /// <summary>
    /// Activates, deactivates or toggles an expression (by name or file); returns the new state. With
    /// <paramref name="revertAfter"/> it goes back to its previous state after that time.
    /// </summary>
    public async Task<bool> SetExpressionAsync(string nameOrFile, SwitchMode mode, double fadeTime = 0.25, CancellationToken ct = default,
        TimeSpan? revertAfter = null)
    {
        nameOrFile = Steps.Require(nameOrFile, "Expression");
        var expressions = await GetExpressionsAsync(ct).ConfigureAwait(false);
        var expression = expressions.FirstOrDefault(e => string.Equals(e.File, nameOrFile, StringComparison.OrdinalIgnoreCase))
            ?? expressions.FirstOrDefault(e => string.Equals(e.DisplayName, nameOrFile, StringComparison.OrdinalIgnoreCase));
        var file = expression?.File ?? (nameOrFile.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase) ? nameOrFile : nameOrFile + ".exp3.json");
        var before = expression?.Active ?? false;
        var active = Steps.Desired(mode, before);
        await RequestAsync("ExpressionActivationRequest", new { expressionFile = file, fadeTime = Math.Clamp(fadeTime, 0, 2), active }, ct)
            .ConfigureAwait(false);
        var target = RevertExpression + file;
        if (revertAfter is { } after)
            _reverts.Schedule(target, after, token => RequestAsync("ExpressionActivationRequest",
                new { expressionFile = file, fadeTime = Math.Clamp(fadeTime, 0, 2), active = before }, token));
        else
            _reverts.Cancel(target);
        return active;
    }

    /// <summary>Deactivates every active expression; returns how many.</summary>
    public async Task<int> RemoveAllExpressionsAsync(double fadeTime = 0.25, CancellationToken ct = default)
    {
        _reverts.CancelAll(RevertExpression);
        var active = (await GetExpressionsAsync(ct).ConfigureAwait(false)).Where(e => e.Active).ToArray();
        foreach (var e in active)
            await RequestAsync("ExpressionActivationRequest", new { expressionFile = e.File, fadeTime = Math.Clamp(fadeTime, 0, 2), active = false }, ct)
                .ConfigureAwait(false);
        return active.Length;
    }

    /// <summary>
    /// Injects tracking parameter values. VTube Studio keeps injected values for about a second, so with
    /// <paramref name="hold"/> the values are re-sent every <see cref="IntegrationsTimings.InjectInterval"/> until it elapses.
    /// In "set" mode a parameter belongs to the newest injection: an older hold stops re-sending it instead of fighting.
    /// </summary>
    public async Task InjectParametersAsync(IReadOnlyList<VtsParameterValue> values, bool add, bool faceFound, TimeSpan hold,
        CancellationToken ct = default)
    {
        VtsProtocol.ValidateInjection(values);
        var owner = new object();
        if (!add)
            foreach (var v in values) _parameterOwners[v.Id] = owner;
        try
        {
            DateTime? until = null;
            while (true)
            {
                var mine = add ? values : values.Where(v => _parameterOwners.TryGetValue(v.Id, out var o) && ReferenceEquals(o, owner)).ToArray();
                if (mine.Count == 0) return; // a newer injection took over every parameter
                await RequestAsync("InjectParameterDataRequest", new
                {
                    faceFound,
                    mode = add ? "add" : "set",
                    parameterValues = mine.Select(v => new { id = v.Id, value = v.Value, weight = v.Weight }).ToArray(),
                }, ct).ConfigureAwait(false);
                until ??= DateTime.UtcNow + hold; // counted from when VTube Studio has the values
                var left = until.Value - DateTime.UtcNow;
                // VTube Studio keeps the last values for about a second, so a remainder this short needs no resend; it would
                // also make Task.Delay return at once and spin out a burst of requests.
                if (left < MinimumResendWait) return;
                await Task.Delay(left < Timings.InjectInterval ? left : Timings.InjectInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!add)
                foreach (var v in values) _parameterOwners.TryRemove(new KeyValuePair<string, object>(v.Id, owner));
        }
    }

    /// <summary>Tints art meshes. With no name/tag filters every mesh is tinted.</summary>
    public Task<int> ColorTintAsync((int R, int G, int B, int A) color, IReadOnlyList<string> nameContains,
        IReadOnlyList<string> tagContains, double mixWithSceneLighting = 1, CancellationToken ct = default) =>
        ColorTintAsync(color, new VtsTintMatcher { NameContains = nameContains, TagContains = tagContains }, mixWithSceneLighting, null, ct);

    public Task<int> ResetColorTintAsync(CancellationToken ct = default)
    {
        lock (_tints) _tints.Clear();
        _reverts.Cancel(RevertTint);
        return ColorTintAsync((255, 255, 255, 255), VtsTintMatcher.All, 1, null, ct, remember: false);
    }

    /// <summary>Loads an item into the scene; returns its instance id.</summary>
    public Task<string> LoadItemAsync(string fileName, double positionX, double positionY, double size, double rotation,
        double fadeTime, int? order, bool unloadWhenPluginDisconnects, CancellationToken ct = default) =>
        LoadItemAsync(new VtsItemLoad(fileName)
        {
            PositionX = positionX, PositionY = positionY, Size = size, Rotation = rotation, FadeTime = fadeTime, Order = order,
            UnloadWhenPluginDisconnects = unloadWhenPluginDisconnects,
        }, ct);

    /// <summary>
    /// Unloads items by instance id and/or file name, every item in the scene (<paramref name="all"/>), or every item
    /// Cuebot loaded (<paramref name="onlyLoadedByCuebot"/>); returns how many.
    /// </summary>
    public async Task<int> UnloadItemsAsync(IReadOnlyList<string> instanceIds, IReadOnlyList<string> fileNames, bool all,
        CancellationToken ct = default, bool onlyLoadedByCuebot = false)
    {
        if (!all && !onlyLoadedByCuebot && instanceIds.Count == 0 && fileNames.Count == 0)
            throw new ArgumentException("Enter an item instance id or file name.");
        foreach (var id in instanceIds) _reverts.Cancel(RevertItem + id);
        var data = await RequestAsync("ItemUnloadRequest", new
        {
            unloadAllInScene = all,
            unloadAllLoadedByThisPlugin = onlyLoadedByCuebot,
            allowUnloadingItemsLoadedByUserOrOtherPlugins = !onlyLoadedByCuebot,
            instanceIDs = instanceIds.ToArray(),
            fileNames = fileNames.ToArray(),
        }, ct).ConfigureAwait(false);
        return data.Array("unloadedItems").Count();
    }

    /// <summary>
    /// Splits "Unload item" entries into instance ids and file names. VTube Studio ignores entries it does not find,
    /// so an entry that is neither a known instance nor a known file is sent as both (animation folders and Live2D
    /// items have file names without a dot, and instance ids have none either).
    /// </summary>
    internal (string[] Ids, string[] Files) SplitItemEntries(IReadOnlyList<string> entries)
    {
        var ids = new List<string>();
        var files = new List<string>();
        foreach (var entry in entries)
        {
            var isInstance = _items.Any(i => i.InstanceId == entry);
            var isFile = _itemFiles.Any(f => f.FileName == entry) || _items.Any(i => i.FileName == entry);
            if (isInstance) ids.Add(entry);
            if (isFile) files.Add(entry);
            if (!isInstance && !isFile)
            {
                ids.Add(entry);
                files.Add(entry);
            }
        }
        return (ids.Distinct().ToArray(), files.Distinct().ToArray());
    }

    /// <summary>Moves an item (by instance id, or the first instance of a file name); null values stay unchanged.</summary>
    public Task MoveItemAsync(string instanceIdOrFile, double timeInSeconds, string fadeMode, double? positionX, double? positionY,
        double? size, double? rotation, CancellationToken ct = default) =>
        MoveItemsAsync([Steps.Require(instanceIdOrFile, "Item")], timeInSeconds, fadeMode, positionX, positionY, size, rotation, null, null, ct);

    /// <summary>
    /// Moves items (instance ids or file names; a file name moves every instance of it). Null values stay unchanged
    /// (sent as -1000, which VTube Studio ignores); <paramref name="order"/> null keeps the layer order.
    /// </summary>
    public async Task MoveItemsAsync(IReadOnlyList<string> instanceIdsOrFiles, double timeInSeconds, string fadeMode, double? positionX,
        double? positionY, double? size, double? rotation, int? order, bool? flip, CancellationToken ct = default)
    {
        var ids = await ResolveItemsAsync(instanceIdsOrFiles, ct).ConfigureAwait(false);
        const double keep = -1000;
        var data = await RequestAsync("ItemMoveRequest", new
        {
            itemsToMove = ids.Take(64).Select(id => new
            {
                itemInstanceID = id,
                timeInSeconds = Math.Clamp(timeInSeconds, 0, 30),
                fadeMode = fadeMode.Trim().Length > 0 ? fadeMode.Trim() : "linear",
                positionX = positionX ?? keep,
                positionY = positionY ?? keep,
                size = size ?? keep,
                rotation = rotation ?? keep,
                order = order ?? (int)keep,
                setFlip = flip is not null,
                flip = flip ?? false,
                userCanStop = true,
            }).ToArray(),
        }, ct).ConfigureAwait(false);
        var failed = data.Array("movedItems").Where(m => m.Bool("success") == false).ToArray();
        if (failed.Length > 0)
        {
            var first = failed[0];
            var errorId = (int)(first.Num("errorID") ?? 0);
            throw new IntegrationRequestException(
                $"VTube Studio could not move item {first.Str("itemInstanceID")} (error {errorId}). {VtsProtocol.Hint(errorId)}".TrimEnd(), errorId);
        }
    }

    /// <summary>Instance ids for entries that are instance ids or file names (every instance of a file).</summary>
    private async Task<string[]> ResolveItemsAsync(IReadOnlyList<string> entries, CancellationToken ct)
    {
        if (entries.Count == 0) throw new ArgumentException("Enter an item instance id or file name.");
        if (entries.Any(e => !_items.Any(i => i.InstanceId == e)))
            await RefreshItemsAsync(includeFiles: false, ct).ConfigureAwait(false);
        var ids = new List<string>();
        foreach (var entry in entries)
        {
            var byFile = _items.Where(i => string.Equals(i.FileName, entry, StringComparison.OrdinalIgnoreCase)).Select(i => i.InstanceId).ToArray();
            if (_items.Any(i => i.InstanceId == entry)) ids.Add(entry);
            else if (byFile.Length > 0) ids.AddRange(byFile);
            else ids.Add(entry); // VTube Studio reports it as not found
        }
        return ids.Distinct().ToArray();
    }
}
