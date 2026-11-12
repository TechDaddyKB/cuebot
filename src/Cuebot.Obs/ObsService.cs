using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Obs.Protocol;
using Cuebot.Obs.Streamlabs;

namespace Cuebot.Obs;

/// <summary>
/// The app's OBS connections: the default one (stored at the top level of obs.json) plus any additional named ones.
/// Members that do not take a connection act on the default connection, so single-OBS code keeps working.
/// </summary>
public sealed class ObsService : IConnectable, IAsyncDisposable
{
    /// <summary>First retry delay; it doubles on each failure up to 30 s (see ObsConnection).</summary>
    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly JsonFile<ObsSettings> _settingsFile;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly object _gate = new();
    private ObsConnection[] _connections;
    private bool _started;
    private int _disposed;
    private ProfileService? _profile;

    public ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, DefaultReconnectDelay)
    {
    }

    /// <summary>
    /// DI constructor: also carries the Streamlabs Desktop service so the OBS page can show it and
    /// <see cref="StartAsync"/> starts it (the app host only starts <see cref="ObsService"/>).
    /// </summary>
    public ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log, StreamlabsDesktopService streamlabsDesktop)
        : this(settings, bus, log, DefaultReconnectDelay) =>
        StreamlabsDesktop = streamlabsDesktop;

    /// <summary>
    /// DI constructor with the action profile: high-volume OBS events are subscribed only while an enabled trigger needs
    /// them, and renamed scenes, sources and filters are followed in saved steps.
    /// </summary>
    public ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log, StreamlabsDesktopService streamlabsDesktop,
        ProfileService profile)
        : this(settings, bus, log, streamlabsDesktop) =>
        AttachProfile(profile);

    /// <summary>The Streamlabs Desktop connection registered alongside OBS, when the module was added through DI.</summary>
    public StreamlabsDesktopService? StreamlabsDesktop { get; }

    internal ObsService(JsonFile<ObsSettings> settings, EventBus bus, AppLog log, TimeSpan reconnectDelay)
    {
        _settingsFile = settings;
        _bus = bus;
        _log = log;
        _reconnectDelay = reconnectDelay;
        if (string.IsNullOrWhiteSpace(settings.Value.Name)) settings.Value.Name = "OBS";
        Default = Create(settings.Value, isDefault: true);
        _connections = [Default, .. settings.Value.Connections.Select(c => Create(c, isDefault: false))];
    }

    /// <summary>The connection used when no connection name is given.</summary>
    public ObsConnection Default { get; }

    /// <summary>All connections, the default first.</summary>
    public IReadOnlyList<ObsConnection> Connections => _connections;

    /// <summary>Raised when a connection is added, removed or renamed.</summary>
    public event Action? ConnectionsChanged;

    // ---- IConnectionStatus: the default connection, plus a summary of the others ----

    public string Name => "OBS";
    public ConnectionState State => Default.State;

    public string StatusMessage
    {
        get
        {
            var others = _connections.Skip(1).ToArray();
            if (others.Length == 0) return Default.StatusMessage;
            var connected = others.Count(c => c.State == ConnectionState.Connected);
            return $"{Default.StatusMessage} (+{connected}/{others.Length} other connections up)";
        }
    }

    public event Action? StatusChanged;

    // ---- Default-connection shortcuts ----

    /// <summary>Live settings of the default connection (and the list of extra ones); call <see cref="SaveSettings"/> after changing them.</summary>
    public ObsSettings Settings => _settingsFile.Value;

    public IReadOnlyList<string> Scenes => Default.Scenes;
    public IReadOnlyList<string> Inputs => Default.Inputs;
    public string? CurrentScene => Default.CurrentScene;

    /// <summary>Raised when the default connection's cached lists change.</summary>
    public event Action? ScenesChanged;

    public bool IsConnected => Default.IsConnected;

    public void SaveSettings() => _settingsFile.ScheduleSave();

    /// <summary>Starts the auto-connect loop of every connection. Returns immediately.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObsConnection[] connections;
        lock (_gate)
        {
            _started = true;
            connections = _connections;
        }
        foreach (var connection in connections) connection.StartAsync(ct);
        StreamlabsDesktop?.StartAsync(ct);
        return Task.CompletedTask;
    }

    public Task ConnectAsync(CancellationToken ct = default) => Default.ConnectAsync(ct);
    public Task DisconnectAsync() => Default.DisconnectAsync();

    public Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default) =>
        Default.RequestAsync(requestType, requestData, ct);

    public Task SetCurrentSceneAsync(string sceneName, CancellationToken ct = default) => Default.SetCurrentSceneAsync(sceneName, ct);

    public Task<bool> SetSourceVisibilityAsync(string sceneName, string sourceName, VisibilityMode mode, CancellationToken ct = default) =>
        Default.SetSourceVisibilityAsync(sceneName, sourceName, mode, ct);

    public Task<bool> SetInputMuteAsync(string inputName, MuteMode mode, CancellationToken ct = default) =>
        Default.SetInputMuteAsync(inputName, mode, ct);

    public Task SetInputTextAsync(string inputName, string text, CancellationToken ct = default) =>
        Default.SetInputTextAsync(inputName, text, ct);

    public Task ControlStreamAsync(OutputCommand command, CancellationToken ct = default) => Default.ControlStreamAsync(command, ct);
    public Task ControlRecordAsync(OutputCommand command, CancellationToken ct = default) => Default.ControlRecordAsync(command, ct);

    // ---- Connection management ----

    /// <summary>The connection named <paramref name="name"/> (case-insensitive); the default one when empty.</summary>
    /// <exception cref="ObsConnectionException">No connection has that name.</exception>
    public ObsConnection Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default;
        var trimmed = name.Trim();
        return _connections.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? throw new ObsConnectionException($"There is no OBS connection named '{trimmed}'.");
    }

    /// <summary>Names of all connections, the default first (for editor suggestions).</summary>
    public IEnumerable<string> ConnectionNames => _connections.Select(c => c.Name);

    /// <summary>Distinct values of a cached list across all connections (for editor suggestions).</summary>
    public IEnumerable<string> All(Func<ObsConnection, IEnumerable<string>> list) =>
        _connections.SelectMany(list).Distinct(StringComparer.Ordinal);

    /// <summary>Adds a connection with a unique name derived from <paramref name="name"/>, saves, and starts it when the service runs.</summary>
    public ObsConnection AddConnection(string name = "OBS 2", string host = "127.0.0.1", int port = 4455)
    {
        ObsConnection connection;
        bool started;
        lock (_gate)
        {
            var settings = new ObsConnectionSettings { Name = UniqueName(name), Host = host, Port = port };
            Settings.Connections.Add(settings);
            connection = Create(settings, isDefault: false);
            _connections = [.. _connections, connection];
            started = _started;
        }
        SaveSettings();
        UpdateSubscriptions();
        if (started) connection.StartAsync();
        ConnectionsChanged?.Invoke();
        StatusChanged?.Invoke();
        return connection;
    }

    /// <summary>Disconnects and removes an additional connection. The default connection cannot be removed.</summary>
    public async Task RemoveConnectionAsync(ObsConnection connection)
    {
        if (connection.IsDefault) throw new InvalidOperationException("The default OBS connection cannot be removed.");
        lock (_gate)
        {
            if (!_connections.Contains(connection)) return;
            _connections = _connections.Where(c => c != connection).ToArray();
            Settings.Connections.Remove(connection.Settings);
        }
        SaveSettings();
        await connection.DisposeAsync().ConfigureAwait(false);
        ConnectionsChanged?.Invoke();
        StatusChanged?.Invoke();
    }

    /// <summary>Renames a connection; fails when another connection already has the name.</summary>
    public void RenameConnection(ObsConnection connection, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A connection needs a name.");
        lock (_gate)
        {
            if (_connections.Any(c => c != connection && string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Another OBS connection is already named '{trimmed}'.");
            connection.Settings.Name = trimmed;
        }
        SaveSettings();
        UpdateSubscriptions();
        ConnectionsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        // DI disposes this instance twice (as ObsService and as IConnectionStatus).
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_profile is { } profile) profile.Changed -= UpdateSubscriptions;
        foreach (var connection in _connections) await connection.DisposeAsync().ConfigureAwait(false);
    }

    private string UniqueName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "OBS" : name.Trim();
        var candidate = baseName;
        for (var i = 2; _connections.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{baseName} ({i})";
        return candidate;
    }

    private ObsConnection Create(ObsConnectionSettings settings, bool isDefault)
    {
        var connection = new ObsConnection(settings, SaveSettings, _bus, _log, _reconnectDelay, isDefault);
        connection.StatusChanged += () => StatusChanged?.Invoke();
        if (isDefault) connection.ScenesChanged += () => ScenesChanged?.Invoke();
        connection.Renamed += rename => _ = FollowRenameAsync(connection, rename);
        return connection;
    }

    // ---- Profile-driven behaviour ----

    /// <summary>Watches the action profile (tests attach one directly; DI uses the constructor that takes it).</summary>
    internal void AttachProfile(ProfileService profile)
    {
        _profile = profile;
        profile.Changed += UpdateSubscriptions;
        UpdateSubscriptions();
    }

    /// <summary>
    /// Works out, per connection, which high-volume obs-websocket events enabled triggers need (and the talking
    /// detectors they ask for) and applies it; OBS is told through Reidentify, without reconnecting.
    /// </summary>
    internal void UpdateSubscriptions()
    {
        if (_profile is not { } profile) return;
        var snapshot = profile.Snapshot;
        foreach (var connection in _connections)
        {
            var triggers = snapshot.Actions.Where(a => a.Enabled)
                .SelectMany(a => a.Triggers)
                .Where(t => t.Enabled && t.Type.StartsWith("obs.", StringComparison.Ordinal) && TriggerTargets(t.Settings, connection))
                .ToArray();
            var plan = ObsTriggers.PlanSubscriptions(triggers);
            connection.SetHighVolumeSubscriptions(plan.Bits, plan.PublishVolumeMeters);
            connection.SetTalkingDetectors(plan.Talking);
        }
    }

    /// <summary>A trigger listens to a connection when its connection setting is empty (any) or names it.</summary>
    private static bool TriggerTargets(IReadOnlyDictionary<string, string> settings, ObsConnection connection) =>
        !settings.TryGetValue(SubActions.ObsSubAction.ConnectionKey, out var name) || string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), connection.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A step (or trigger) is about a connection when its connection setting names it, or is empty and it is the default.</summary>
    private static bool StepTargets(IReadOnlyDictionary<string, string> settings, ObsConnection connection) =>
        !settings.TryGetValue(SubActions.ObsSubAction.ConnectionKey, out var name) || string.IsNullOrWhiteSpace(name)
            ? connection.IsDefault
            : string.Equals(name.Trim(), connection.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After OBS renamed a scene, source or filter, rewrites the old name in this connection's saved OBS steps and
    /// triggers (exact matches only; values with %variables% are left alone). Off when the connection's
    /// <see cref="ObsConnectionSettings.FollowRenames"/> is.
    /// </summary>
    internal async Task<int> FollowRenameAsync(ObsConnection connection, ObsRename rename)
    {
        if (_profile is not { } profile || !connection.Settings.FollowRenames) return 0;
        string[] keys = rename.Kind switch
        {
            ObsRenameKind.Scene => ["scene", "group", "source", "destinationScene"],
            ObsRenameKind.Input => ["source", "input", "group"],
            _ => ["filter"],
        };
        // Count on the snapshot first, so an unrelated rename does not save the profile.
        if (Rewrite(profile.Snapshot.Actions, dryRun: true) == 0) return 0;
        var changed = 0;
        try
        {
            await profile.EditAsync(p => changed = Rewrite(p.Actions, dryRun: false)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("OBS", $"Could not update steps after OBS renamed '{rename.OldName}': {ex.Message}");
            return 0;
        }
        _log.Info("OBS", $"OBS renamed {rename.Kind.ToString().ToLowerInvariant()} '{rename.OldName}' to '{rename.NewName}'; updated {changed} step(s) and trigger(s).");
        return changed;

        int Rewrite(IEnumerable<ActionDefinition> actions, bool dryRun)
        {
            var count = 0;
            foreach (var action in actions)
            {
                foreach (var trigger in action.Triggers.Where(t => t.Type.StartsWith("obs.", StringComparison.Ordinal)))
                    count += Fix(trigger.Settings, dryRun);
                count += Steps(action.SubActions, dryRun);
            }
            return count;
        }

        int Steps(IEnumerable<SubActionDefinition> steps, bool dryRun)
        {
            var count = 0;
            foreach (var step in steps)
            {
                if (step.Type.StartsWith("obs.", StringComparison.Ordinal)) count += Fix(step.Parameters, dryRun);
                foreach (var branch in step.Branches.Values) count += Steps(branch, dryRun);
            }
            return count;
        }

        int Fix(Dictionary<string, string> values, bool dryRun)
        {
            if (!StepTargets(values, connection)) return 0;
            if (rename.Kind == ObsRenameKind.Filter &&
                !(values.TryGetValue("source", out var source) && source == rename.SourceName)) return 0;
            var hits = keys.Where(k => values.TryGetValue(k, out var value) && value == rename.OldName).ToArray();
            if (!dryRun) foreach (var key in hits) values[key] = rename.NewName;
            return hits.Length > 0 ? 1 : 0;
        }
    }
}
