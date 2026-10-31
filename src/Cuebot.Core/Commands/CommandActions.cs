using Cuebot.Core.Actions;

namespace Cuebot.Core.Commands;

internal static class CommandCategories
{
    public const string Commands = "Commands";
    public static readonly IReadOnlyList<string> States = ["Enable", "Disable", "Toggle"];

    /// <summary>Applies an Enable/Disable/Toggle choice to the current state.</summary>
    public static bool Apply(string choice, bool current) => choice.Trim().ToLowerInvariant() switch
    {
        "enable" or "enabled" or "true" or "on" => true,
        "disable" or "disabled" or "false" or "off" => false,
        "toggle" => !current,
        _ => throw new ArgumentException($"Unknown state '{choice}' (use Enable, Disable or Toggle)"),
    };
}

/// <summary>"Command triggered" and "Command cooldown" triggers for the commands manager.</summary>
public sealed class CommandTriggers(CommandStore store) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger("command.triggered", "Command triggered", CommandService.TriggeredEvent),
        Trigger("command.cooldown", "Command cooldown", CommandService.CooldownEvent),
    ];

    private EventTriggerType Trigger(string id, string name, string eventType) =>
        new(id, CommandCategories.Commands, name, eventType,
            [
                new ParameterDescriptor("command", "Command")
                {
                    Suggestions = () => store.Snapshot.Select(c => c.Name),
                    Help = "Command name (or id); leave empty for every command",
                },
            ],
            (e, s, _) => Matches(e.Args, s["command"]));

    internal static bool Matches(IReadOnlyDictionary<string, object?> args, string wanted)
    {
        wanted = wanted.Trim();
        return wanted.Length == 0
               || string.Equals(args.GetString("commandName"), wanted, StringComparison.OrdinalIgnoreCase)
               || string.Equals(args.GetString("commandId"), wanted, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Base for sub-actions that look a command up by name or id; unknown commands fail the action.</summary>
public abstract class CommandSubActionBase(CommandStore store) : ISubActionHandler
{
    protected CommandStore Store => store;
    public abstract string TypeId { get; }
    public string Category => CommandCategories.Commands;
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }

    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct);

    protected ParameterDescriptor CommandParameter() =>
        new("command", "Command") { DefaultValue = "%commandName%", Suggestions = () => store.Snapshot.Select(c => c.Name) };

    protected ParameterDescriptor GroupParameter() =>
        new("group", "Command group") { Suggestions = () => store.Groups };

    protected CommandDefinition Require(string idOrName) =>
        store.Find(idOrName) ?? throw new InvalidOperationException($"No command named '{idOrName}'");
}

public sealed class GetCommandStateSubAction(CommandStore store) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.getState";
    public override string DisplayName => "Get command state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??= [CommandParameter()];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var command = Require(p["command"]);
        context.Args["commandEnabled"] = command.Enabled;
        context.Args["commandState"] = command.Enabled;
        context.Args["commandStateName"] = command.Name;
        context.Args["commandStateId"] = command.Id.ToString();
        context.Args["commandStateGroup"] = command.Group;
        context.Args["commandCounter"] = Store.GetCounter(command.Id);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetCommandStateSubAction(CommandStore store) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.setState";
    public override string DisplayName => "Set command state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        CommandParameter(),
        new("state", "State", ParameterKind.Choice) { DefaultValue = "Enable", Choices = CommandCategories.States },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var command = Require(p["command"]);
        var enabled = CommandCategories.Apply(p["state"], command.Enabled);
        Store.SetEnabled(command.Id, enabled);
        context.Args["commandEnabled"] = enabled;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class GetCommandGroupStateSubAction(CommandStore store) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.getGroupState";
    public override string DisplayName => "Get command group state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??= [GroupParameter()];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var group = Store.Snapshot.Where(c => string.Equals(c.Group, p["group"].Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var enabled = group.Count(c => c.Enabled);
        context.Args["commandGroupEnabled"] = group.Length > 0 && enabled == group.Length;
        context.Args["commandGroupEnabledCount"] = enabled;
        context.Args["commandGroupCount"] = group.Length;
        context.Args["commandGroupState"] = enabled == 0 ? 0 : enabled == group.Length ? 1 : 2;
        context.Args["commandsEnabled"] = group.Where(c => c.Enabled).Select(c => c.Id.ToString()).ToList();
        context.Args["commandsDisabled"] = group.Where(c => !c.Enabled).Select(c => c.Id.ToString()).ToList();
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetCommandGroupStateSubAction(CommandStore store) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.setGroupState";
    public override string DisplayName => "Set command group state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        GroupParameter(),
        new("state", "State", ParameterKind.Choice) { DefaultValue = "Enable", Choices = CommandCategories.States },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["group"].Trim();
        var group = Store.Snapshot.Where(c => string.Equals(c.Group, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Toggle flips the group as a whole: enable unless every command is already enabled.
        var enabled = CommandCategories.Apply(p["state"], group.Length > 0 && group.All(c => c.Enabled));
        context.Args["commandGroupCount"] = Store.SetGroupEnabled(name, enabled);
        context.Args["commandGroupEnabled"] = enabled;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Lists commands into %&lt;argument&gt;% (joined), %&lt;argument&gt;List% (a list) and %&lt;argument&gt;Count%. Like
/// Streamer.bot it can include commands whose Include option is off, and keep only commands the running user may use.
/// </summary>
public sealed class GetCommandsSubAction(CommandStore store, CommandService? service = null) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.getCommands";
    public override string DisplayName => "Get commands";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("argument", "Save in variable") { DefaultValue = "commands", Help = "Also sets %<argument>List% and %<argument>Count%" },
        new("group", "Only this group") { Suggestions = () => Store.Groups, Help = "Leave empty (or All) for every group" },
        new("show", "List", ParameterKind.Choice) { DefaultValue = "First alias", Choices = ["First alias", "All aliases", "Names"] },
        new("separator", "Separator") { DefaultValue = ", " },
        new("includeDisabled", "Include disabled commands", ParameterKind.Boolean) { DefaultValue = "false" },
        new("includeAll", "Include all (ignore each command's Include option)", ParameterKind.Boolean) { DefaultValue = "false" },
        new("onlyPermitted", "Only commands the user may use", ParameterKind.Boolean) { DefaultValue = "false", Help = "Checks the run's user (%user%, %userId%, roles) against each command's permissions" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var group = p["group"].Trim();
        if (group.Equals("All", StringComparison.OrdinalIgnoreCase)) group = "";
        var includeDisabled = p.GetBool("includeDisabled");
        var includeAll = p.GetBool("includeAll");
        var onlyPermitted = p.GetBool("onlyPermitted") && service is not null;
        var commands = Store.Snapshot
            .Where(c => (includeAll || c.Include) && (includeDisabled || c.Enabled))
            .Where(c => group.Length == 0 || string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase))
            .Where(c => !onlyPermitted || service!.IsPermitted(c, context.Args))
            .ToArray();
        var items = p["show"] switch
        {
            "Names" => commands.Select(c => c.Name),
            "All aliases" => commands.SelectMany(c => c.Commands),
            _ => commands.Select(c => c.Commands.FirstOrDefault() ?? c.Name),
        };
        var list = items.Where(s => s.Trim().Length > 0).ToArray();
        var name = p["argument"].Trim().Length > 0 ? p["argument"].Trim() : "commands";
        context.Args[name] = string.Join(p["separator"], list);
        context.Args[name + "List"] = list.ToList();
        context.Args[name + "Count"] = list.Length;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class ResetCommandCountersSubAction(CommandStore store) : CommandSubActionBase(store)
{
    public override string TypeId => "commands.resetCounters";
    public override string DisplayName => "Reset command counters";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        CommandParameter(),
        new("reset", "Reset", ParameterKind.Choice) { DefaultValue = "Counter", Choices = ["Counter", "User counters", "Both"] },
        new("platform", "User's platform") { DefaultValue = "%platform%" },
        new("user", "Only this user id") { Help = "Leave empty to reset every user's counter" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var command = Require(p["command"]);
        var reset = p["reset"];
        if (reset is "Counter" or "Both") Store.ResetCounter(command.Id);
        if (reset is "User counters" or "Both")
        {
            var user = p["user"].Trim();
            Store.ResetUserCounters(command.Id, user.Length == 0 ? null : CommandStore.UserKey(p["platform"].Trim(), user, user));
        }
        return Task.FromResult(SubActionResult.Next);
    }
}
