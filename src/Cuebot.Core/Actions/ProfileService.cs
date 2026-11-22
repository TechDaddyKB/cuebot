using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Transfer;

namespace Cuebot.Core.Actions;

/// <summary>Immutable copy of the profile used by the engine and timers, so UI edits never race execution.</summary>
public sealed record ProfileSnapshot(IReadOnlyList<ActionDefinition> Actions, IReadOnlyList<TimerDefinition> Timers)
{
    /// <summary>User-created queues (the built-in "Default" queue is not listed).</summary>
    public IReadOnlyList<QueueDefinition> Queues { get; init; } = [];

    /// <summary>
    /// The action with this name, or else with this id: references are names in the editors, but the Assistant and
    /// the REST API often write the id (a deck button saved with save_deck, say), and both should run the action.
    /// </summary>
    public ActionDefinition? FindAction(string name) =>
        Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? (Guid.TryParse(name, out var id) ? FindAction(id) : null);

    public ActionDefinition? FindAction(Guid id) => Actions.FirstOrDefault(a => a.Id == id);

    public QueueDefinition? FindQueue(string name) =>
        Queues.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Owns actions.json. <see cref="Profile"/> is the live, editable object (UI thread only);
/// call <see cref="NotifyChanged"/> after editing to persist and publish a new snapshot.
/// </summary>
public sealed class ProfileService : IDisposable
{
    private readonly JsonFile<BotProfile>? _file;
    private BotProfile _profile;

    /// <summary>Per action, the name references to it currently use (lags the action's name while that name is unusable).</summary>
    private readonly Dictionary<Guid, string> _referenceNames = [];

    public ProfileService(AppPaths? paths, AppLog log)
    {
        if (paths is not null)
        {
            _file = new JsonFile<BotProfile>(paths.File("actions.json"), log);
            _profile = _file.Value;
            _profile.Queues ??= []; // older or hand-edited files
        }
        else
        {
            _profile = new BotProfile();
        }
        foreach (var action in _profile.Actions) _referenceNames[action.Id] = action.Name;
        Snapshot = CreateSnapshot();
    }

    public BotProfile Profile => _profile;

    public ProfileSnapshot Snapshot { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// An action's name changed (old, new) and the steps naming it were updated. Stores outside the profile that name
    /// actions (deck buttons, hotkeys, chat quick actions) follow it here. Raised before <see cref="Changed"/>.
    /// </summary>
    public event Action<string, string>? ActionRenamed;

    /// <summary>
    /// Marshals edits that originate outside the UI (e.g. the "Set action state" sub-action) onto the thread that
    /// owns <see cref="Profile"/>. The app sets it to post to its UI thread; when null, edits run inline.
    /// </summary>
    public Func<Action, Task>? EditDispatcher { get; set; }

    public void NotifyChanged()
    {
        var renames = FollowActionRenames();
        Snapshot = CreateSnapshot();
        _file?.ScheduleSave();
        foreach (var (oldName, newName) in renames) ActionRenamed?.Invoke(oldName, newName);
        Changed?.Invoke();
    }

    /// <summary>
    /// Every edit path (editor, API, assistant, portal, import) ends here, so renames are detected by id. The editor
    /// publishes each keystroke: while the typed name is empty, dynamic or another action's, references wait at the last
    /// usable name, so typing past another action's name never captures its references.
    /// </summary>
    private List<(string Old, string New)> FollowActionRenames()
    {
        var renames = new List<(string Old, string New)>();
        var actions = _profile.Actions;
        bool HeldByOther(string name, ActionDefinition self) =>
            actions.Any(a => a != self && string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var action in actions)
        {
            if (!_referenceNames.TryGetValue(action.Id, out var oldName))
            {
                _referenceNames[action.Id] = action.Name;
                continue;
            }
            var newName = action.Name;
            if (oldName == newName || !TransferReferences.IsLiteral(newName) || HeldByOther(newName, action)) continue;
            _referenceNames[action.Id] = newName;
            if (HeldByOther(oldName, action)) continue;   // the old name now means that other action
            foreach (var owner in actions)
                TransferReferences.Rewrite(owner, (kind, value) =>
                    kind == TransferKind.Action && string.Equals(value, oldName.Trim(), StringComparison.OrdinalIgnoreCase) ? newName : null);
            renames.Add((oldName, newName));
        }
        if (_referenceNames.Count > actions.Count)
        {
            var live = actions.Select(a => a.Id).ToHashSet();
            foreach (var id in _referenceNames.Keys.Where(id => !live.Contains(id)).ToArray()) _referenceNames.Remove(id);
        }
        return renames;
    }

    /// <summary>Applies <paramref name="edit"/> to the live profile on the owning thread, then publishes the change.</summary>
    public Task EditAsync(Action<BotProfile> edit)
    {
        void Apply()
        {
            edit(_profile);
            NotifyChanged();
        }
        if (EditDispatcher is { } dispatch) return dispatch(Apply);
        Apply();
        return Task.CompletedTask;
    }

    /// <summary>Enables or disables an action by name. Returns false when no such action exists.</summary>
    public async Task<bool> SetActionEnabledAsync(string name, bool enabled)
    {
        var found = false;
        await EditAsync(p =>
        {
            var action = p.Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (action is null) return;
            found = true;
            action.Enabled = enabled;
        }).ConfigureAwait(false);
        return found;
    }

    public void Flush() => _file?.Flush();

    private ProfileSnapshot CreateSnapshot()
    {
        var copy = Json.Clone(_profile);
        return new ProfileSnapshot(copy.Actions, copy.Timers) { Queues = copy.Queues ?? [] };
    }

    public void Dispose() => _file?.Dispose();
}
