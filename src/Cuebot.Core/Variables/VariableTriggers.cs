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
        Trigger("variables.userUpdated", "User variable updated", VariableEvents.UserUpdated, user: true),
    ];

    public const string Either = "Either";
    public const string Yes = "Yes";
    public const string No = "No";

    private static EventTriggerType Trigger(string id, string name, string eventType, bool user = false) =>
        new(id, Categories.Variables, name, eventType,
            [
                new ParameterDescriptor("variable", "Variable name") { Help = "Leave empty for any variable" },
                new ParameterDescriptor("persisted", "Persisted", ParameterKind.Choice) { DefaultValue = Either, Choices = [Either, Yes, No] },
                .. user
                    ? new[]
                    {
                        new ParameterDescriptor("userLogin", "User login") { Help = "Leave empty for any user" },
                        new ParameterDescriptor("platform", "Platform") { Help = "Leave empty for any platform" },
                    }
                    : [],
            ],
            (e, settings, _) => Matches(e.Args, settings, user));

    private static bool Matches(IReadOnlyDictionary<string, object?> args, ParameterValues settings, bool user)
    {
        var variable = settings["variable"].Trim();
        if (variable.Length > 0 && !string.Equals(args.GetValueOrDefault("variableName") as string, variable, StringComparison.OrdinalIgnoreCase))
            return false;
        var persisted = args.GetValueOrDefault("persisted") is true;
        switch (settings["persisted"].Trim())
        {
            case Yes when !persisted:
            case No when persisted:
                return false;
        }
        if (!user) return true;
        var login = settings["userLogin"].Trim().TrimStart('@');
        var platform = settings["platform"].Trim();
        return (login.Length == 0 || string.Equals(args.GetValueOrDefault("userName") as string, login, StringComparison.OrdinalIgnoreCase))
               && (platform.Length == 0 || string.Equals(args.GetValueOrDefault("platform") as string, platform, StringComparison.OrdinalIgnoreCase));
    }

    private void OnGlobal(GlobalVariableChange c)
    {
        var type = c.Kind switch
        {
            VariableChangeKind.Created => VariableEvents.GlobalCreated,
            VariableChangeKind.Deleted => VariableEvents.GlobalDeleted,
            _ => VariableEvents.GlobalUpdated,
        };
        var args = new Dictionary<string, object?>
        {
            ["variableName"] = c.Name,
            ["oldValue"] = c.OldValue,
            ["newValue"] = c.NewValue,
            ["persisted"] = c.Persisted,
            // Streamer.bot names.
            ["global.name"] = c.Name,
            ["global.persisted"] = c.Persisted,
        };
        if (c.Kind != VariableChangeKind.Deleted)
        {
            args["global.lastWrite"] = DateTime.Now;
            if (c.Kind == VariableChangeKind.Created) args["global.value"] = c.NewValue;
            else
            {
                args["global.newValue"] = c.NewValue;
                args["global.oldValue"] = c.OldValue;
            }
        }
        _bus.Publish(new BotEvent(type, args));
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
            ["global.name"] = c.Name,
            ["global.newValue"] = c.NewValue,
            ["global.oldValue"] = c.OldValue,
            ["global.persisted"] = c.Persisted,
            ["global.lastWrite"] = DateTime.Now,
            ["global.userId"] = c.UserId,
            ["global.userType"] = c.Platform,
            ["global.userLogin"] = c.UserName,
            ["global.userName"] = c.UserName,
        }));
    }

    public void Dispose()
    {
        _globals.VariableChanged -= OnGlobal;
        _users.Changed -= OnUser;
    }
}
