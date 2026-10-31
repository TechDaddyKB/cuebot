using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Core.Users;

/// <summary>Group added/removed/cleared and users added/removed triggers.</summary>
public sealed class GroupTriggers(GroupStore groups) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger("groups.added", "Group added", GroupEvents.Added),
        Trigger("groups.removed", "Group removed", GroupEvents.Removed),
        Trigger("groups.cleared", "Group cleared", GroupEvents.Cleared),
        Trigger("groups.usersAdded", "Users added to group", GroupEvents.UsersAdded),
        Trigger("groups.usersRemoved", "Users removed from group", GroupEvents.UsersRemoved),
    ];

    private EventTriggerType Trigger(string id, string name, string eventType) =>
        new(id, UserParameters.Groups, name, eventType,
            [new ParameterDescriptor("group", "Group") { Suggestions = () => groups.Names, Help = "Leave empty for every group" }],
            (e, s, _) => s["group"].Trim().Length == 0
                         || string.Equals(e.Args.GetValueOrDefault("groupName") as string, s["group"].Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Shared parameters (group, platform, user, user id) of the per-user group sub-actions.</summary>
public abstract class GroupUserSubActionBase(GroupStore groups) : ISubActionHandler
{
    protected GroupStore Groups => groups;
    public abstract string TypeId { get; }
    public string Category => UserParameters.Groups;
    public abstract string DisplayName { get; }
    public virtual IReadOnlyList<string> Branches => [];
    public virtual IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??= [.. CommonParameters()];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct);

    protected IEnumerable<ParameterDescriptor> CommonParameters() =>
    [
        new("group", "Group") { Suggestions = () => groups.Names },
        new("platform", "Platform") { DefaultValue = "%platform%" },
        new("user", "User name") { DefaultValue = "%user%" },
        new("userId", "User id") { DefaultValue = "%userId%", Help = "Matched first when known; otherwise the name is used" },
    ];

    protected static (string Group, string Platform, string User, string UserId) Read(ParameterValues p)
    {
        var group = p["group"].Trim();
        if (group.Length == 0) throw new ArgumentException("No group given");
        return (group, UserParameters.Clean(p["platform"]), UserParameters.Clean(p["user"]).TrimStart('@'), UserParameters.Clean(p["userId"]));
    }
}

public sealed class AddUserToGroupSubAction(GroupStore groups) : GroupUserSubActionBase(groups)
{
    public override string TypeId => "groups.addUser";
    public override string DisplayName => "Add user to group";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        .. CommonParameters(),
        new("create", "Create the group if missing", ParameterKind.Boolean) { DefaultValue = "true" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, platform, user, userId) = Read(p);
        if (!p.GetBool("create", true) && !Groups.Exists(group)) throw new InvalidOperationException($"No group named '{group}'");
        var added = Groups.AddUser(group, platform, userId, user, createGroup: true);
        context.Args["groupUserAdded"] = added;
        context.Args["addResult"] = added; // Streamer.bot name
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class RemoveUserFromGroupSubAction(GroupStore groups) : GroupUserSubActionBase(groups)
{
    public override string TypeId => "groups.removeUser";
    public override string DisplayName => "Remove user from group";

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, platform, user, userId) = Read(p);
        var removed = Groups.RemoveUser(group, platform, userId, user);
        context.Args["groupUserRemoved"] = removed;
        context.Args["removeResult"] = removed; // Streamer.bot name
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Runs the True or False branch depending on membership, and stores the answer in an argument.</summary>
public sealed class UserInGroupSubAction(GroupStore groups) : GroupUserSubActionBase(groups)
{
    public const string True = "True";
    public const string False = "False";

    public override string TypeId => "groups.userInGroup";
    public override string DisplayName => "User in group";
    public override IReadOnlyList<string> Branches { get; } = [True, False];
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        .. CommonParameters(),
        new("argument", "Save the answer in variable") { DefaultValue = "inGroup" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, platform, user, userId) = Read(p);
        var inGroup = Groups.Contains(group, platform, userId, user);
        if (p["argument"].Trim().Length > 0) context.Args[p["argument"].Trim()] = inGroup;
        // Streamer.bot names.
        context.Args["inGroup"] = inGroup;
        context.Args["foundUser"] = user;
        context.Args["foundUserName"] = user.ToLowerInvariant();
        context.Args["foundUserId"] = userId;
        context.Args["foundUserType"] = platform.ToLowerInvariant();
        return Task.FromResult(SubActionResult.TakeBranch(inGroup ? True : False));
    }
}

public sealed class ClearGroupSubAction(GroupStore groups) : ISubActionHandler
{
    public string TypeId => "groups.clear";
    public string Category => UserParameters.Groups;
    public string DisplayName => "Clear users from group";
    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
        [new("group", "Group") { Suggestions = () => groups.Names }];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var removed = groups.Clear(p["group"]);
        if (removed < 0) throw new InvalidOperationException($"No group named '{p["group"]}'");
        context.Args["groupUsersRemoved"] = removed;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Script functions for groups. User functions take <c>group</c>, <c>platform</c>, <c>user</c> and <c>userId</c>;
/// the last three default to the action's %platform%, %user% and %userId% arguments.
/// </summary>
public sealed class GroupScriptFunctions(GroupStore groups) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("groups.add", "Add a user to a group (created if missing); returns true when added", ["group", "platform", "user", "userId"],
            (ctx, a, _) => Result(UserFrom(ctx, a) is var u && groups.AddUser(Group(a), u.Platform, u.UserId, u.User, createGroup: true))),
        new("groups.remove", "Remove a user from a group; returns true when removed", ["group", "platform", "user", "userId"],
            (ctx, a, _) => Result(UserFrom(ctx, a) is var u && groups.RemoveUser(Group(a), u.Platform, u.UserId, u.User))),
        new("groups.contains", "Whether a user is in a group", ["group", "platform", "user", "userId"],
            (ctx, a, _) => Result(UserFrom(ctx, a) is var u && groups.Contains(Group(a), u.Platform, u.UserId, u.User))),
        new("groups.list", "Names of all groups", [], (_, _, _) => Task.FromResult<object?>(groups.Names)),
        new("groups.users", "Members of a group as {platform, userId, userName} objects", ["group"],
            (_, a, _) => Task.FromResult<object?>(groups.UsersIn(Group(a))
                .Select(m => new Dictionary<string, object?> { ["platform"] = m.Platform, ["userId"] = m.UserId, ["userName"] = m.UserName })
                .ToArray())),
        new("groups.create", "Create an empty group; returns false if it exists", ["group"], (_, a, _) => Result(groups.Add(Group(a)))),
        new("groups.delete", "Delete a group; returns false if missing", ["group"], (_, a, _) => Result(groups.Remove(Group(a)))),
        new("groups.exists", "Whether a group exists", ["group"], (_, a, _) => Result(groups.Exists(Group(a)))),
        new("groups.clear", "Remove every user from a group; returns how many were removed (-1 if missing)", ["group"],
            (_, a, _) => Task.FromResult<object?>(groups.Clear(Group(a)))),
    ];

    private static Task<object?> Result(bool value) => Task.FromResult<object?>(value);

    private static string Group(JsonElement a) =>
        a.GetString("group").Trim() is { Length: > 0 } g ? g : throw new ScriptException("Missing 'group'");

    private static (string Platform, string User, string UserId) UserFrom(ActionRunContext ctx, JsonElement a)
    {
        string Value(string key) => a.GetElement(key) is not null
            ? a.GetString(key).Trim()
            : Commands.ChatArgs.GetString(ctx.Args, key);
        return (Value("platform"), Value("user").TrimStart('@'), Value("userId"));
    }
}
