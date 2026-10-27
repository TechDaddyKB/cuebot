using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Core.BuiltIn;

/// <summary>Script functions every script runtime gets: arguments, globals, logging, running actions.</summary>
public sealed class CoreScriptFunctions : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("args.get", "Get an action argument (null if missing)", ["name"],
            (ctx, a, _) => Task.FromResult(ctx.Args.GetValueOrDefault(a.GetString("name")))),
        new("args.set", "Set an action argument", ["name", "value"], (ctx, a, _) =>
        {
            ctx.Args[a.GetString("name")] = a.GetElement("value") is { } v ? ScriptArgs.ToClr(v) : null;
            return Task.FromResult<object?>(null);
        }),
        new("args.all", "All action arguments as an object", [],
            (ctx, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>(ctx.Args))),
        new("globals.get", "Get a global variable (null if missing)", ["name"],
            (ctx, a, _) => Task.FromResult<object?>(ctx.Globals.Get(a.GetString("name")))),
        new("globals.set", "Set a global variable", ["name", "value", "persisted"], (ctx, a, _) =>
        {
            ctx.Globals.Set(a.GetString("name"), a.GetString("value"), a.GetBool("persisted", true));
            return Task.FromResult<object?>(null);
        }),
        new("log", "Write to the log", ["message", "level"], (ctx, a, _) =>
        {
            var level = Enum.TryParse<LogLevel>(a.GetString("level", "Info"), true, out var l) ? l : LogLevel.Info;
            ctx.Log.Write(level, ctx.Action.Name, a.GetString("message"));
            return Task.FromResult<object?>(null);
        }),
        new("actions.run", "Run another action by name and wait for it; returns success", ["name"], async (ctx, a, ct) =>
        {
            var name = a.GetString("name");
            var target = ctx.Engine.FindAction(name) ?? throw new ScriptException($"No action named '{name}'");
            return await ctx.Engine.RunAsync(target, ctx.Args, ctx.Depth + 1, $"Script in '{ctx.Action.Name}'", ct);
        }),
    ];
}
