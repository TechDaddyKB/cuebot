using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;

namespace Cuebot.Core.Users;

internal static class UserParameters
{
    public const string Users = "Users";
    public const string Groups = "Groups";

    public static ParameterDescriptor Platform(ChatHub hub, string defaultValue = ChatHub.AllPlatforms) =>
        new("platform", "Platform", ParameterKind.Choice) { DefaultValue = defaultValue, Choices = [ChatHub.AllPlatforms, .. hub.Platforms] };

    public static bool PlatformMatches(IReadOnlyDictionary<string, object?> args, string platform) =>
        platform.Length == 0 || string.Equals(platform, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
        || string.Equals(args.GetString("platform"), platform, StringComparison.OrdinalIgnoreCase);

    /// <summary>A parameter whose %variable% did not resolve (argument missing) counts as empty.</summary>
    public static string Clean(string value)
    {
        var v = value.Trim();
        return v.Length > 2 && v[0] == '%' && v[^1] == '%' && v.IndexOf('%', 1) == v.Length - 1 ? "" : v;
    }
}

/// <summary>"First words" and "Present viewers" triggers.</summary>
public sealed class UserTriggers(ChatHub hub) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("users.firstWords", UserParameters.Users, "First words", UserEvents.FirstWords,
            [UserParameters.Platform(hub)], (e, s, _) => UserParameters.PlatformMatches(e.Args, s["platform"])),
        new EventTriggerType("users.presentViewers", UserParameters.Users, "People in chat (every few minutes)", UserEvents.PresentViewers,
            [UserParameters.Platform(hub)], (e, s, _) => UserParameters.PlatformMatches(e.Args, s["platform"])),
    ];
}

public sealed class ResetFirstWordsSubAction(UserRegistry users, ChatHub hub) : ISubActionHandler
{
    public string TypeId => "users.resetFirstWords";
    public string Category => UserParameters.Users;
    public string DisplayName => "Reset first words";
    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??= [UserParameters.Platform(hub)];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["firstWordsReset"] = users.ResetFirstWords(p["platform"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Picks a random present viewer into <c>%randomUser%</c>, <c>%randomUserName%</c>, <c>%randomUserId%</c>...</summary>
public sealed class AddRandomPresentUserSubAction(UserRegistry users, ChatHub hub) : ISubActionHandler
{
    public string TypeId => "users.addRandomPresentUser";
    public string Category => UserParameters.Users;
    public string DisplayName => "Pick a random recent chatter";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        UserParameters.Platform(hub),
        new("argument", "Variable name start") { DefaultValue = "randomUser" },
        new("excludeCaller", "Exclude the triggering user", ParameterKind.Boolean) { DefaultValue = "true" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        string? exclude = null;
        if (p.GetBool("excludeCaller", true))
        {
            var args = (IReadOnlyDictionary<string, object?>)context.Args;
            exclude = UserRecord.KeyOf(args.GetString("platform"), args.GetString("userId"),
                args.GetString("userName") is { Length: > 0 } n ? n : args.GetString("user"));
        }
        var user = users.RandomPresentUser(p["platform"], exclude);
        var prefix = p["argument"].Trim().Length > 0 ? p["argument"].Trim() : "randomUser";
        context.Args[prefix] = user?.DisplayName ?? "";
        context.Args[prefix + "Name"] = user?.UserName ?? "";
        context.Args[prefix + "Id"] = user?.UserId ?? "";
        context.Args[prefix + "Platform"] = user?.Platform ?? "";
        context.Args[prefix + "Found"] = user is not null;
        return Task.FromResult(SubActionResult.Next);
    }
}
