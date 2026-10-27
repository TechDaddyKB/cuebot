using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Actions;

/// <summary>Immutable copy of the profile used by the engine and timers, so UI edits never race execution.</summary>
public sealed record ProfileSnapshot(IReadOnlyList<ActionDefinition> Actions, IReadOnlyList<TimerDefinition> Timers)
{
    /// <summary>User-created queues (the built-in "Default" queue is not listed).</summary>
    public IReadOnlyList<QueueDefinition> Queues { get; init; } = [];

    public ActionDefinition? FindAction(string name) =>
        Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

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
        Snapshot = CreateSnapshot();
    }

    public BotProfile Profile => _profile;

    public ProfileSnapshot Snapshot { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// Marshals edits that originate outside the UI (e.g. the "Set action state" sub-action) onto the thread that
    /// owns <see cref="Profile"/>. The app sets it to post to its UI thread; when null, edits run inline.
    /// </summary>
    public Func<Action, Task>? EditDispatcher { get; set; }

    public void NotifyChanged()
    {
        Snapshot = CreateSnapshot();
        _file?.ScheduleSave();
        Changed?.Invoke();
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
