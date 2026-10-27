using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

namespace Cuebot.Core.Variables;

internal static class UserParameters
{
    private static readonly string[] KnownPlatforms = ["twitch", "youtube", "kick", "rumble"];

    public static ParameterDescriptor[] For(UserVariables users) =>
    [
        new("platform", "Platform")
        {
            Help = "Empty = the platform of the trigger",
            Suggestions = () => KnownPlatforms.Concat(users.Platforms()).Distinct(StringComparer.OrdinalIgnoreCase),
        },
        new("userId", "User id") { Help = "Empty = the user who triggered the action (or look up by user name)" },
        new("userName", "User name") { Help = "Used to find a known user when no id is given" },
    ];

    public static UserTarget? Find(UserVariables users, ActionRunContext context, ParameterValues p) =>
        UserTarget.Find(users, context.Args, p["platform"], p["userId"], p["userName"]);

    public static UserTarget Require(UserVariables users, ActionRunContext context, ParameterValues p) =>
        UserTarget.Require(users, context.Args, p["platform"], p["userId"], p["userName"]);
}

/// <summary>Get a user's variable into an argument.</summary>
public sealed class GetUserVariableSubAction(UserVariables users) : ISubActionHandler
{
    public string TypeId => "core.getUserVariable";
    public string Category => Categories.Variables;
    public string DisplayName => "Get user variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name"),
        .. UserParameters.For(users),
        new("destination", "Store in argument") { Help = "Defaults to the variable name; use it as %argument%" },
        new("defaultValue", "Default value") { Help = "Used when the user or variable does not exist; empty leaves the argument unset" },
        new("autoType", "Auto type", ParameterKind.Boolean) { DefaultValue = "true", Help = "Numbers stay numbers" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = VariableNames.Require(p["name"]);
        var destination = p["destination"].Trim().Length > 0 ? VariableNames.Require(p["destination"], "Argument name") : name;
        var autoType = p.GetBool("autoType", true);
        var target = UserParameters.Find(users, context, p);
        if (target is { } t && users.TryGet(t.Platform, t.UserId, name, out var value))
        {
            context.Args[destination] = autoType && value is string s ? VariableValues.AutoType(s) : value;
        }
        else if (p["defaultValue"].Length > 0)
        {
            context.Args[destination] = autoType ? VariableValues.AutoType(p["defaultValue"]) : p["defaultValue"];
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Set, increment or decrement a user's variable.</summary>
public sealed class SetUserVariableSubAction(UserVariables users) : ISubActionHandler
{
    public string TypeId => "core.setUserVariable";
    public string Category => Categories.Variables;
    public string DisplayName => "Set user variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name"),
        .. UserParameters.For(users),
        new("mode", "Mode", ParameterKind.Choice) { DefaultValue = SetModes.Value, Choices = SetModes.All },
        new("value", "Value") { Help = "For Increment/Decrement: the amount (empty = 1)" },
        new("persisted", "Keep after restart", ParameterKind.Boolean) { DefaultValue = "true" },
        new("autoType", "Auto type", ParameterKind.Boolean) { DefaultValue = "true", Help = "Store numbers and true/false as typed values" },
        new("storeIn", "Also store in argument") { Help = "Optional argument that receives the new value" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = VariableNames.Require(p["name"]);
        var target = UserParameters.Require(users, context, p);
        var persist = p.GetBool("persisted", true);
        object? value;
        if (SetModes.IsIncrement(p["mode"], out var sign))
        {
            value = users.Increment(target.Platform, target.UserId, target.UserName, name, sign * SetModes.Amount(p["value"]), persist);
        }
        else
        {
            value = p.GetBool("autoType", true) ? VariableValues.AutoType(p["value"]) : p["value"];
            users.Set(target.Platform, target.UserId, target.UserName, name, value, persist);
        }
        if (p["storeIn"].Trim().Length > 0) context.Args[VariableNames.Require(p["storeIn"], "Argument name")] = value;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Delete a user's variable, or the variable for every user.</summary>
public sealed class DeleteUserVariableSubAction(UserVariables users) : ISubActionHandler
{
    public string TypeId => "core.deleteUserVariable";
    public string Category => Categories.Variables;
    public string DisplayName => "Delete user variable";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("name", "Variable name"),
        .. UserParameters.For(users),
        new("allUsers", "For all users", ParameterKind.Boolean) { DefaultValue = "false", Help = "Delete it from every user of the platform (all platforms when Platform is empty)" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = VariableNames.Require(p["name"]);
        if (p.GetBool("allUsers"))
        {
            var platform = p["platform"].Trim();
            users.DeleteForAllUsers(name, platform.Length > 0 ? platform : null);
        }
        else if (UserParameters.Find(users, context, p) is { } t)
        {
            users.Delete(t.Platform, t.UserId, name);
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class ClearTemporaryUserVariablesSubAction(UserVariables users) : ISubActionHandler
{
    public string TypeId => "core.clearTemporaryUserVariables";
    public string Category => Categories.Variables;
    public string DisplayName => "Clear temporary user variables";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("platform", "Platform") { Help = "Empty = all platforms" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var platform = p["platform"].Trim();
        users.ClearTemporary(platform.Length > 0 ? platform : null);
        return Task.FromResult(SubActionResult.Next);
    }
}
