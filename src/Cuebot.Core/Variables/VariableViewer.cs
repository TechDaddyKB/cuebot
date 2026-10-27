using Cuebot.Core.Actions;

namespace Cuebot.Core.Variables;

public enum VariableScope { Persisted, Temporary, User }

/// <summary>A change seen by the viewer; <see cref="Platform"/>/<see cref="UserId"/> are set for user variables.</summary>
public sealed record VariableViewerChange(VariableScope Scope, VariableChangeKind Kind, string Name, string? Platform, string? UserId);

/// <summary>
/// One place for the Global Variable Viewer: persisted / temporary globals and per-platform user variables,
/// edits that honour Auto Type, and a single change event. Events are raised on the thread that made the
/// change; the UI marshals to its own thread.
/// </summary>
public sealed class VariableViewer : IDisposable
{
    private readonly GlobalVariables _globals;
    private readonly UserVariables _users;

    public VariableViewer(GlobalVariables globals, UserVariables users)
    {
        _globals = globals;
        _users = users;
        _globals.VariableChanged += OnGlobal;
        _users.Changed += OnUser;
    }

    public event Action<VariableViewerChange>? Changed;

    public IReadOnlyList<GlobalVariableInfo> PersistedGlobals() => _globals.Snapshot(persisted: true);

    public IReadOnlyList<GlobalVariableInfo> TemporaryGlobals() => _globals.Snapshot(persisted: false);

    public IReadOnlyList<string> UserPlatforms() => _users.Platforms();

    public IReadOnlyList<UserVariableInfo> UserVariables(string? platform = null, bool? persisted = null) =>
        _users.Snapshot(platform, persisted: persisted);

    /// <summary>Sets a global from the editor. With <paramref name="autoType"/> "5" is stored as a number.</summary>
    public void SetGlobal(string name, string value, bool persisted, bool autoType = true) =>
        _globals.SetValue(name.Trim(), autoType ? VariableValues.AutoType(value) : value, persisted);

    public bool DeleteGlobal(string name) => _globals.Remove(name);

    public int DeleteAllGlobals(bool persisted) => _globals.Clear(persisted);

    public void SetUserVariable(string platform, string userId, string? userName, string name, string value, bool persisted, bool autoType = true) =>
        _users.Set(platform, userId, userName, name.Trim(), autoType ? VariableValues.AutoType(value) : value, persisted);

    public bool DeleteUserVariable(string platform, string userId, string name) => _users.Delete(platform, userId, name);

    /// <summary>"Delete selected for all users": removes this variable name from every user of the platform.</summary>
    public int DeleteUserVariableForAllUsers(string name, string? platform = null) => _users.DeleteForAllUsers(name, platform);

    public int DeleteAllUserVariables(string? platform = null) => _users.Clear(platform);

    private void OnGlobal(GlobalVariableChange c) =>
        Changed?.Invoke(new VariableViewerChange(c.Persisted ? VariableScope.Persisted : VariableScope.Temporary, c.Kind, c.Name, null, null));

    private void OnUser(UserVariableChange c) =>
        Changed?.Invoke(new VariableViewerChange(VariableScope.User, c.Kind, c.Name, c.Platform, c.UserId));

    public void Dispose()
    {
        _globals.VariableChanged -= OnGlobal;
        _users.Changed -= OnUser;
    }
}
