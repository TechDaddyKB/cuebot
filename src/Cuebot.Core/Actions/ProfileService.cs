using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Actions;

/// <summary>Immutable copy of the profile used by the engine and timers, so UI edits never race execution.</summary>
public sealed record ProfileSnapshot(IReadOnlyList<ActionDefinition> Actions, IReadOnlyList<TimerDefinition> Timers)
{
    public ActionDefinition? FindAction(string name) =>
        Actions.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
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

    public void NotifyChanged()
    {
        Snapshot = CreateSnapshot();
        _file?.ScheduleSave();
        Changed?.Invoke();
    }

    public void Flush() => _file?.Flush();

    private ProfileSnapshot CreateSnapshot()
    {
        var copy = Json.Clone(_profile);
        return new ProfileSnapshot(copy.Actions, copy.Timers);
    }

    public void Dispose() => _file?.Dispose();
}
