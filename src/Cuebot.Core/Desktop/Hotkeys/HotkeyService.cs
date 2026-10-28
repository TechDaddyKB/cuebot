using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Desktop.Hotkeys;

/// <summary>A user-defined global hotkey.</summary>
public sealed class HotkeyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Text form of a <see cref="HotkeyGesture"/>, e.g. "Ctrl+Shift+F1".</summary>
    public string Gesture { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Optional action (by name) run directly on press, besides the "Hotkey pressed" trigger.</summary>
    public string Action { get; set; } = "";

    /// <summary>Name of the <see cref="HotkeyGroup"/> it is shown under; empty for none.</summary>
    public string Group { get; set; } = "";
}

/// <summary>A named roll-up of hotkeys on the page (Streamer.bot's hotkey groups); disabling it disables its hotkeys.</summary>
public sealed class HotkeyGroup
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>hotkeys.json.</summary>
public sealed class HotkeySettings
{
    public int Version { get; set; } = 1;
    public List<HotkeyDefinition> Hotkeys { get; set; } = [];
    public List<HotkeyGroup> Groups { get; set; } = [];
}

public static class HotkeyEvents
{
    public const string Pressed = "hotkeys.pressed";
}

/// <summary>Live registration state of one hotkey for the page.</summary>
public sealed record HotkeyState(Guid Id, bool Registered, string Message);

/// <summary>
/// Owns the hotkey definitions (hotkeys.json) and keeps the OS registrations in sync through an
/// <see cref="IGlobalHotkeys"/> backend. A press publishes "hotkeys.pressed" (hotkeyId, hotkeyName, hotkey) and
/// runs the bound action when one is set. Starts on <see cref="StartAsync"/> or the first timer tick.
/// </summary>
public sealed class HotkeyService : IPeriodicService, IDisposable
{
    private readonly JsonFile<HotkeySettings>? _file;
    private readonly HotkeySettings _settings;
    private readonly IGlobalHotkeys _backend;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<string, IReadOnlyDictionary<string, object?>, Task>? _runAction;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private Dictionary<Guid, HotkeyState> _states = [];
    private int _started;
    private int _version;

    public HotkeyService(JsonFile<HotkeySettings>? file, IGlobalHotkeys backend, EventBus bus, AppLog log,
        Func<string, IReadOnlyDictionary<string, object?>, Task>? runAction = null)
    {
        _file = file;
        _settings = file?.Value ?? new HotkeySettings();
        _backend = backend;
        _bus = bus;
        _log = log;
        _runAction = runAction;
        // A group named only by its hotkeys (hand-edited hotkeys.json) still gets a header.
        foreach (var name in _settings.Hotkeys.Select(h => h.Group.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            if (FindGroupLocked(name) is null) _settings.Groups.Add(new HotkeyGroup { Name = name });
        _backend.Pressed += OnPressed;
    }

    /// <summary>Raised after definitions or registration states change (any thread).</summary>
    public event Action? Changed;

    public string BackendName => _backend.BackendName;
    public string BackendStatus => _backend.Status;

    public IReadOnlyList<HotkeyDefinition> Hotkeys
    {
        get { lock (_gate) return Json.Clone(_settings.Hotkeys); }
    }

    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return _settings.Hotkeys.Select(h => h.Name).Where(n => n.Length > 0).ToArray(); }
    }

    /// <summary>The groups, in the order they were created.</summary>
    public IReadOnlyList<HotkeyGroup> Groups
    {
        get { lock (_gate) return Json.Clone(_settings.Groups); }
    }

    public HotkeyState? State(Guid id)
    {
        lock (_gate) return _states.GetValueOrDefault(id);
    }

    /// <summary>Adds or replaces a definition (matched by id), saves, and re-registers.</summary>
    public Task SaveAsync(HotkeyDefinition definition)
    {
        lock (_gate)
        {
            var copy = Json.Clone(definition);
            copy.Group = copy.Group.Trim();
            if (copy.Group.Length > 0)
            {
                // A new group name (typed in, or from an import) creates the group; a known one keeps its spelling.
                if (FindGroupLocked(copy.Group) is { } group) copy.Group = group.Name;
                else _settings.Groups.Add(new HotkeyGroup { Name = copy.Group });
            }
            var index = _settings.Hotkeys.FindIndex(h => h.Id == copy.Id);
            if (index >= 0) _settings.Hotkeys[index] = copy;
            else _settings.Hotkeys.Add(copy);
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return ReapplyIfStartedAsync();
    }

    public Task RemoveAsync(Guid id)
    {
        lock (_gate)
        {
            if (_settings.Hotkeys.RemoveAll(h => h.Id == id) == 0) return Task.CompletedTask;
            _states.Remove(id);
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return ReapplyIfStartedAsync();
    }

    /// <summary>Creates an empty group. Names are unique, ignoring case.</summary>
    public Task AddGroupAsync(string name)
    {
        name = name.Trim();
        lock (_gate)
        {
            if (name.Length == 0) throw new ArgumentException("A group needs a name.", nameof(name));
            if (FindGroupLocked(name) is not null) throw new ArgumentException($"There is already a group called '{name}'.", nameof(name));
            _settings.Groups.Add(new HotkeyGroup { Name = name });
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Renames a group and moves its hotkeys along.</summary>
    public Task RenameGroupAsync(string name, string newName)
    {
        newName = newName.Trim();
        lock (_gate)
        {
            var group = FindGroupLocked(name) ?? throw new ArgumentException($"There is no group called '{name}'.", nameof(name));
            if (newName.Length == 0) throw new ArgumentException("A group needs a name.", nameof(newName));
            if (FindGroupLocked(newName) is { } other && other != group)
                throw new ArgumentException($"There is already a group called '{newName}'.", nameof(newName));
            foreach (var hotkey in _settings.Hotkeys.Where(h => string.Equals(h.Group, group.Name, StringComparison.OrdinalIgnoreCase)))
                hotkey.Group = newName;
            group.Name = newName;
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Deletes a group; its hotkeys stay, without a group.</summary>
    public Task RemoveGroupAsync(string name)
    {
        bool hadDisabled;
        lock (_gate)
        {
            if (FindGroupLocked(name) is not { } group) return Task.CompletedTask;
            _settings.Groups.Remove(group);
            hadDisabled = !group.Enabled;
            foreach (var hotkey in _settings.Hotkeys.Where(h => string.Equals(h.Group, group.Name, StringComparison.OrdinalIgnoreCase)))
                hotkey.Group = "";
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return hadDisabled ? ReapplyIfStartedAsync() : Task.CompletedTask;
    }

    /// <summary>Enables or disables every hotkey of a group at once (their own Enabled flags are kept).</summary>
    public Task SetGroupEnabledAsync(string name, bool enabled)
    {
        lock (_gate)
        {
            var group = FindGroupLocked(name) ?? throw new ArgumentException($"There is no group called '{name}'.", nameof(name));
            if (group.Enabled == enabled) return Task.CompletedTask;
            group.Enabled = enabled;
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        return ReapplyIfStartedAsync();
    }

    private HotkeyGroup? FindGroupLocked(string name) =>
        _settings.Groups.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public Task StartAsync()
    {
        Interlocked.Exchange(ref _started, 1);
        return ApplyAsync();
    }

    void IPeriodicService.Tick(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0) _ = ApplyAsync();
    }

    /// <summary>Registers every enabled, valid hotkey with the backend (replacing earlier registrations).</summary>
    public async Task ApplyAsync()
    {
        var version = Interlocked.Increment(ref _version);
        await _applyLock.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _version)) return;   // a newer apply is queued behind us
            List<HotkeyRegistration> registrations = [];
            var states = new Dictionary<Guid, HotkeyState>();
            HashSet<string> disabledGroups;
            lock (_gate) disabledGroups = _settings.Groups.Where(g => !g.Enabled).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hotkey in Hotkeys)
            {
                if (!hotkey.Enabled)
                {
                    states[hotkey.Id] = new HotkeyState(hotkey.Id, false, "Disabled");
                    continue;
                }
                if (disabledGroups.Contains(hotkey.Group))
                {
                    states[hotkey.Id] = new HotkeyState(hotkey.Id, false, $"Group '{hotkey.Group}' is disabled");
                    continue;
                }
                if (!HotkeyGesture.TryParse(hotkey.Gesture, out var gesture, out var error))
                {
                    states[hotkey.Id] = new HotkeyState(hotkey.Id, false, hotkey.Gesture.Trim().Length == 0 ? "No key combination set" : error);
                    continue;
                }
                if (registrations.Any(r => r.Gesture == gesture))
                {
                    states[hotkey.Id] = new HotkeyState(hotkey.Id, false, $"{gesture} is already used by another hotkey");
                    continue;
                }
                registrations.Add(new HotkeyRegistration(hotkey.Id.ToString("N"), hotkey.Name, gesture));
            }
            try
            {
                foreach (var result in await _backend.ApplyAsync(registrations))
                {
                    if (!Guid.TryParseExact(result.Id, "N", out var id)) continue;
                    var message = result.Ok
                        ? result.ActualTrigger is { Length: > 0 } actual ? "Registered as " + actual : "Registered"
                        : result.Error ?? "Not registered";
                    states[id] = new HotkeyState(id, result.Ok, message);
                    if (!result.Ok) _log.Warn("Hotkeys", $"Hotkey {registrations.First(r => r.Id == result.Id).Gesture}: {message}");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Hotkeys", "Could not register hotkeys", ex);
                foreach (var r in registrations) states[Guid.ParseExact(r.Id, "N")] = new HotkeyState(Guid.ParseExact(r.Id, "N"), false, ex.Message);
            }
            lock (_gate) _states = states;
        }
        finally
        {
            _applyLock.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>Acts as if the hotkey was pressed (the page's Test button, and tests).</summary>
    public bool Simulate(Guid id) => Fire(id);

    private void OnPressed(string registrationId)
    {
        if (Guid.TryParseExact(registrationId, "N", out var id)) Fire(id);
    }

    private bool Fire(Guid id)
    {
        HotkeyDefinition? hotkey;
        lock (_gate) hotkey = _settings.Hotkeys.FirstOrDefault(h => h.Id == id) is { } h ? Json.Clone(h) : null;
        if (hotkey is null) return false;
        var gesture = HotkeyGesture.TryParse(hotkey.Gesture, out var g, out _) ? g.ToString() : hotkey.Gesture;
        var args = new Dictionary<string, object?>
        {
            ["hotkeyId"] = hotkey.Id.ToString(),
            ["hotkeyName"] = hotkey.Name,
            ["hotkey"] = gesture,
            // Streamer.bot Key Press names.
            ["key"] = g.Key,
            ["modifier"] = Cuebot.Core.Desktop.Input.MouseClickService.ModifierText(g.Modifiers),
            ["hasCtrl"] = g.Modifiers.HasFlag(HotkeyModifiers.Ctrl),
            ["hasAlt"] = g.Modifiers.HasFlag(HotkeyModifiers.Alt),
            ["hasShift"] = g.Modifiers.HasFlag(HotkeyModifiers.Shift),
        };
        _bus.Publish(new BotEvent(HotkeyEvents.Pressed, args));
        if (hotkey.Action.Trim().Length > 0 && _runAction is not null)
        {
            _ = RunBoundAsync(hotkey.Action.Trim(), args);
        }
        return true;
    }

    private async Task RunBoundAsync(string action, IReadOnlyDictionary<string, object?> args)
    {
        try { await _runAction!(action, args); }
        catch (Exception ex) { _log.Error("Hotkeys", $"Running '{action}' failed", ex); }
    }

    private Task ReapplyIfStartedAsync() => Volatile.Read(ref _started) == 1 ? ApplyAsync() : Task.CompletedTask;

    /// <summary>Matches a trigger filter against a press: empty = any; else the hotkey's name, id or key combination.</summary>
    public static bool Matches(string filter, IReadOnlyDictionary<string, object?> args)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        string Arg(string key) => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        if (string.Equals(Arg("hotkeyName"), filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(Arg("hotkeyId"), filter, StringComparison.OrdinalIgnoreCase)) return true;
        return HotkeyGesture.TryParse(filter, out var wanted, out _) && HotkeyGesture.TryParse(Arg("hotkey"), out var pressed, out _)
               && wanted == pressed;
    }

    public void Dispose()
    {
        _backend.Pressed -= OnPressed;
        _applyLock.Dispose();
    }
}
