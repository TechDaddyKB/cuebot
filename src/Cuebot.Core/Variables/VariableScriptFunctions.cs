using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Core.Variables;

/// <summary>
/// Script access to per-user variables and the extra global operations. User functions take optional
/// "platform", "userId" and "userName"; empty ones mean the user who triggered the action.
/// </summary>
public sealed class VariableScriptFunctions(UserVariables users) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("userVars.get", "Get a user variable (default when missing)", ["name", "platform", "userId", "userName", "default"], (ctx, a, _) =>
        {
            var name = a.GetString("name");
            var target = Target(ctx, a);
            object? result = target is { } t && users.TryGet(t.Platform, t.UserId, name, out var value)
                ? value
                : a.GetElement("default") is { } d ? VariableValues.FromJson(d) : null;
            return Task.FromResult(result);
        }),
        new("userVars.set", "Set a user variable", ["name", "value", "persisted", "platform", "userId", "userName"], (ctx, a, _) =>
        {
            var t = Require(ctx, a);
            var value = a.GetElement("value") is { } v ? VariableValues.FromJson(v) : null;
            users.Set(t.Platform, t.UserId, t.UserName, Name(a), value, a.GetBool("persisted", true));
            return Task.FromResult<object?>(null);
        }),
        new("userVars.increment", "Add to a numeric user variable (negative to subtract); returns the new value",
            ["name", "amount", "persisted", "platform", "userId", "userName"], (ctx, a, _) =>
        {
            var t = Require(ctx, a);
            return Task.FromResult<object?>(users.Increment(t.Platform, t.UserId, t.UserName, Name(a), Amount(a), a.GetBool("persisted", true)));
        }),
        new("userVars.delete", "Delete a user variable; returns whether it existed", ["name", "platform", "userId", "userName"], (ctx, a, _) =>
            Task.FromResult<object?>(Target(ctx, a) is { } t && users.Delete(t.Platform, t.UserId, Name(a)))),
        new("userVars.deleteForAllUsers", "Delete a variable from every user; returns how many were removed", ["name", "platform"], (_, a, _) =>
        {
            var platform = a.GetString("platform").Trim();
            return Task.FromResult<object?>(users.DeleteForAllUsers(Name(a), platform.Length > 0 ? platform : null));
        }),
        new("userVars.all", "All variables of a user as an object", ["platform", "userId", "userName"], (ctx, a, _) =>
            Task.FromResult<object?>(Target(ctx, a) is { } t
                ? users.GetAll(t.Platform, t.UserId)
                : new Dictionary<string, object?>())),
        new("userVars.clearTemporary", "Delete all temporary user variables; returns how many were removed", ["platform"], (_, a, _) =>
        {
            var platform = a.GetString("platform").Trim();
            return Task.FromResult<object?>(users.ClearTemporary(platform.Length > 0 ? platform : null));
        }),
        new("globals.delete", "Delete a global variable; returns whether it existed", ["name"], (ctx, a, _) =>
            Task.FromResult<object?>(ctx.Globals.Remove(Name(a)))),
        new("globals.increment", "Add to a numeric global variable (negative to subtract); returns the new value",
            ["name", "amount", "persisted"], (ctx, a, _) =>
            Task.FromResult<object?>(ctx.Globals.Increment(Name(a), Amount(a), a.GetBool("persisted", true)))),
        new("globals.getValue", "Get a global variable with its type (number, boolean or text; null if missing)", ["name"], (ctx, a, _) =>
            Task.FromResult(ctx.Globals.GetValue(a.GetString("name")))),
        new("globals.clearTemporary", "Delete all temporary globals; returns how many were removed", [], (ctx, _, _) =>
            Task.FromResult<object?>(ctx.Globals.ClearTemporary())),
        new("text.resolve", "Resolve %args%, ~globals~, formats and $functions()$ in a text", ["text"], (ctx, a, _) =>
            Task.FromResult<object?>(ctx.Resolve(a.GetString("text")))),
    ];

    private UserTarget? Target(ActionRunContext ctx, JsonElement a) =>
        UserTarget.Find(users, ctx.Args, a.GetString("platform"), a.GetString("userId"), a.GetString("userName"));

    private UserTarget Require(ActionRunContext ctx, JsonElement a) =>
        Target(ctx, a) ?? throw new ScriptException("No user: pass platform and userId/userName, or run from a user trigger");

    private static string Name(JsonElement a)
    {
        var name = a.GetString("name").Trim();
        return VariableNames.IsValid(name) ? name : throw new ScriptException($"Invalid variable name '{name}'");
    }

    private static decimal Amount(JsonElement a)
    {
        if (a.GetElement("amount") is not { } amount || amount.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return 1;
        if (amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out var d)) return d;
        return decimal.TryParse(amount.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)
            ? d
            : throw new ScriptException($"Amount '{amount}' is not a number");
    }
}
