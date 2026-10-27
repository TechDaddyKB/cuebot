using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;

namespace Cuebot.Core.Variables;

public static class VariableEvents
{
    public const string GlobalCreated = "variables.globalCreated";
    public const string GlobalUpdated = "variables.globalUpdated";
    public const string GlobalDeleted = "variables.globalDeleted";
    public const string UserUpdated = "variables.userUpdated";
}

/// <summary>
/// Triggers for variable changes. Also bridges <see cref="GlobalVariables"/> and <see cref="UserVariables"/>
/// change events onto the <see cref="EventBus"/> (it is created with the registry, so the bridge is live
/// whenever actions can run). Event arguments: variableName, oldValue, newValue, persisted (+ platform,
/// userId, userName, user for user variables). Beware of actions that change the variable they listen to.
/// </summary>
public sealed class VariableTriggers : ITriggerProvider, IDisposable
{
    private readonly GlobalVariables _globals;
    private readonly UserVariables _users;
    private readonly EventBus _bus;

    public VariableTriggers(GlobalVariables globals, UserVariables users, EventBus bus)
    {
        _globals = globals;
        _users = users;
        _bus = bus;
        _globals.VariableChanged += OnGlobal;
        _users.Changed += OnUser;
    }

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger("variables.globalCreated", "Global variable created", VariableEvents.GlobalCreated),
        Trigger("variables.globalUpdated", "Global variable updated", VariableEvents.GlobalUpdated),
        Trigger("variables.globalDeleted", "Global variable deleted", VariableEvents.GlobalDeleted),
        Trigger("variables.userUpdated", "User variable updated", VariableEvents.UserUpdated),
    ];

    private static EventTriggerType Trigger(string id, string name, string eventType) =>
        new(id, Categories.Variables, name, eventType,
            [new ParameterDescriptor("variable", "Variable name") { Help = "Leave empty for any variable" }],
            (e, settings, _) => settings["variable"].Trim().Length == 0
                || string.Equals(e.Args.GetValueOrDefault("variableName") as string, settings["variable"].Trim(), StringComparison.OrdinalIgnoreCase));

    private void OnGlobal(GlobalVariableChange c)
    {
        var type = c.Kind switch
        {
            VariableChangeKind.Created => VariableEvents.GlobalCreated,
            VariableChangeKind.Deleted => VariableEvents.GlobalDeleted,
            _ => VariableEvents.GlobalUpdated,
        };
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?>
        {
            ["variableName"] = c.Name,
            ["oldValue"] = c.OldValue,
            ["newValue"] = c.NewValue,
            ["persisted"] = c.Persisted,
        }));
    }

    private void OnUser(UserVariableChange c)
    {
        if (c.Kind == VariableChangeKind.Deleted) return;
        _bus.Publish(new BotEvent(VariableEvents.UserUpdated, new Dictionary<string, object?>
        {
            ["variableName"] = c.Name,
            ["oldValue"] = c.OldValue,
            ["newValue"] = c.NewValue,
            ["persisted"] = c.Persisted,
            ["platform"] = c.Platform,
            ["userId"] = c.UserId,
            ["userName"] = c.UserName,
            ["user"] = c.UserName,
        }));
    }

    public void Dispose()
    {
        _globals.VariableChanged -= OnGlobal;
        _users.Changed -= OnUser;
    }
}
