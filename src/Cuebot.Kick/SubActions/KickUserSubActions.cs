using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Users;
using Cuebot.Kick.Events;

namespace Cuebot.Kick.SubActions;

/// <summary>
/// Kick versions of the user-group sub-actions: the same shared groups (groups.json) as everywhere else, with the
/// platform fixed to Kick and the user given by name or numeric Kick id.
/// </summary>
internal abstract class KickGroupSubAction(KickService kick, GroupStore groups) : KickSubAction(kick)
{
    protected GroupStore Groups => groups;

    protected IEnumerable<ParameterDescriptor> Common() =>
    [
        new("group", "Group") { Suggestions = () => groups.Names },
        new("user", "User") { DefaultValue = "%userName%", Help = "Kick user name or numeric user id" },
        new("userId", "User id") { DefaultValue = "%userId%", Help = "Matched first when known; otherwise the name is used" },
    ];

    /// <summary>The group and the user's (id, name); the id comes from a numeric "user", "userId", or users seen in chat.</summary>
    protected (string Group, string UserId, string UserName) Read(ParameterValues p)
    {
        var group = p["group"].Trim();
        if (group.Length == 0) throw new ArgumentException("No group given");
        var user = Clean(p["user"]).TrimStart('@');
        var userId = Clean(p["userId"]);
        if (long.TryParse(user, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
        {
            if (userId.Length == 0) userId = KickArgs.Id(numeric);
            user = "";
        }
        if (userId.Length == 0 && user.Length > 0) userId = Kick.FindKnownUserId(user) ?? "";
        if (userId.Length == 0 && user.Length == 0) throw new ArgumentException("No user given");
        return (group, userId, user);
    }

    /// <summary>Unresolved "%arg%" placeholders count as empty.</summary>
    private static string Clean(string value)
    {
        value = value.Trim();
        return value.StartsWith('%') && value.EndsWith('%') ? "" : value;
    }
}

internal sealed class KickAddUserToGroupSubAction(KickService kick, GroupStore groups) : KickGroupSubAction(kick, groups)
{
    public override string TypeId => "kick.addUserToGroup";
    public override string DisplayName => "Add user to group";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        .. Common(),
        new("create", "Create the group if missing", ParameterKind.Boolean) { DefaultValue = "true" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, userId, user) = Read(p);
        if (!p.GetBool("create", true) && !Groups.Exists(group)) throw new InvalidOperationException($"No group named '{group}'");
        context.Args["groupUserAdded"] = Groups.AddUser(group, KickArgs.Platform, userId, user, createGroup: true);
        context.Args["addResult"] = context.Args["groupUserAdded"];
        return Task.FromResult(SubActionResult.Next);
    }
}

internal sealed class KickRemoveUserFromGroupSubAction(KickService kick, GroupStore groups) : KickGroupSubAction(kick, groups)
{
    public override string TypeId => "kick.removeUserFromGroup";
    public override string DisplayName => "Remove user from group";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??= [.. Common()];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, userId, user) = Read(p);
        context.Args["groupUserRemoved"] = Groups.RemoveUser(group, KickArgs.Platform, userId, user);
        context.Args["removeResult"] = context.Args["groupUserRemoved"];
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Runs the True or False branch depending on membership, and stores the answer in an argument.</summary>
internal sealed class KickUserInGroupSubAction(KickService kick, GroupStore groups) : KickGroupSubAction(kick, groups)
{
    public const string True = "True";
    public const string False = "False";

    public override string TypeId => "kick.userInGroup";
    public override string DisplayName => "User in group";
    public override IReadOnlyList<string> Branches { get; } = [True, False];
    public override IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        .. Common(),
        new("argument", "Store result in argument") { DefaultValue = "inGroup" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var (group, userId, user) = Read(p);
        var inGroup = Groups.Contains(group, KickArgs.Platform, userId, user);
        if (p["argument"].Trim().Length > 0) context.Args[p["argument"].Trim()] = inGroup;
        // Streamer.bot: inGroup plus foundUser, foundUserName, foundUserId, foundUserType
        context.Args.TryAdd("inGroup", inGroup);
        context.Args["foundUser"] = user;
        context.Args["foundUserName"] = user;
        context.Args["foundUserId"] = userId;
        context.Args["foundUserType"] = "kick";
        return Task.FromResult(SubActionResult.TakeBranch(inGroup ? True : False));
    }
}

/// <summary>
/// Random Kick viewers into <c>%randomUser0%</c>, <c>%randomUserName0%</c>, <c>%randomUserId0%</c>... and
/// <c>%randomUserCount%</c> (the first one also as <c>%randomUser%</c>). Kick's API has no viewer list, so the
/// candidates are the present viewers: users who chatted within the presence window (Users settings).
/// </summary>
internal sealed class KickAddRandomUsersSubAction(KickService kick, UserRegistry users) : KickSubAction(kick)
{
    public override string TypeId => "kick.addRandomUsers";
    public override string DisplayName => "Add random users";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("count", "How many", ParameterKind.Number) { DefaultValue = "1" },
        new("excludeBroadcaster", "Leave out the broadcaster and the bot", ParameterKind.Boolean) { DefaultValue = "true" },
        new("excludeCaller", "Leave out the triggering user", ParameterKind.Boolean) { DefaultValue = "false" },
        new("prefix", "Argument prefix") { DefaultValue = "randomUser" },
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (p.GetBool("excludeBroadcaster", true))
            foreach (var account in new[] { Kick.Settings.Broadcaster, Kick.Settings.Bot })
            {
                if (account.UserId > 0) excluded.Add(KickArgs.Id(account.UserId));
                if (account.UserName.Length > 0) excluded.Add(account.UserName);
            }
        if (p.GetBool("excludeCaller"))
            foreach (var key in new[] { "userId", "userName", "user" })
                if (context.Args.TryGetValue(key, out var v) && Convert.ToString(v, CultureInfo.InvariantCulture) is { Length: > 0 } s) excluded.Add(s);

        var candidates = users.PresentUsers(KickArgs.Platform)
            .Where(u => !excluded.Contains(u.UserId) && !excluded.Contains(u.UserName))
            .ToArray();
        var picked = candidates.OrderBy(_ => Random.Shared.Next()).Take(Math.Max(1, p.GetInt("count", 1))).ToArray();
        var prefix = p["prefix"].Trim().Length > 0 ? p["prefix"].Trim() : "randomUser";
        context.Args[prefix + "Count"] = picked.Length;
        context.Args[prefix + "Found"] = picked.Length > 0;
        context.Args[prefix] = picked.FirstOrDefault()?.DisplayName ?? "";
        context.Args[prefix + "Name"] = picked.FirstOrDefault()?.UserName ?? "";
        context.Args[prefix + "Id"] = picked.FirstOrDefault()?.UserId ?? "";
        for (var i = 0; i < picked.Length; i++)
        {
            context.Args[$"{prefix}{i}"] = picked[i].DisplayName.Length > 0 ? picked[i].DisplayName : picked[i].UserName;
            context.Args[$"{prefix}Name{i}"] = picked[i].UserName;
            context.Args[$"{prefix}Id{i}"] = picked[i].UserId;
            context.Args[$"{prefix}Type{i}"] = "kick";
        }
        return Task.FromResult(SubActionResult.Next);
    }
}
