using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Subathon;

public static class SubathonFeatures
{
    public const string TimerCategory = "Subathon timer";
    public const string GoalCategory = "Goals";

    public static IServiceCollection AddSubathonFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new SubathonService(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ChatHub>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        // Ticked every second by the timer service, which also creates it at start-up.
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<SubathonService>());
        services.AddSingleton<ISessionDataSource>(sp => new SubathonSessionData(sp.GetRequiredService<SubathonService>(), TimeProvider.System));
        services.AddSingleton<ISubActionHandler, SubathonControlSubAction>();
        services.AddSingleton<ISubActionHandler, SubathonAddTimeSubAction>();
        services.AddSingleton<ISubActionHandler, SubathonSetTimeSubAction>();
        services.AddSingleton<ISubActionHandler, SubathonMultiplierSubAction>();
        services.AddSingleton<ISubActionHandler, SubathonGetSubAction>();
        services.AddSingleton<ISubActionHandler, GoalAddPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GoalSetPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GoalResetSubAction>();
        services.AddSingleton<ISubActionHandler, GoalMultiplierSubAction>();
        services.AddSingleton<ISubActionHandler, GoalGetSubAction>();
        services.AddSingleton<ITriggerProvider, SubathonTriggers>();
        services.AddSingleton<IScriptFunctionProvider, SubathonScriptFunctions>();
        return services;
    }
}

internal static class SubathonParams
{
    public static ParameterDescriptor Timer(SubathonService s) =>
        new("timer", "Timer") { Help = "The timer's name; empty for the first one", Suggestions = () => s.Timers().Select(t => t.Name) };

    public static ParameterDescriptor Goal(SubathonService s) =>
        new("goal", "Goal") { Help = "The goal's name; empty for the first one", Suggestions = () => s.Goals().Select(g => g.Name) };

    public static string Clean(string? text) => ExtrasText.Clean(text);

    /// <summary>Runs a service call, turning "no such timer/goal" into a readable sub-action error.</summary>
    public static Task<SubActionResult> Do(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        return Task.FromResult(SubActionResult.Next);
    }

    /// <summary>A timer's state in arguments: subathonTimer, subathonState, subathonRemaining, subathonFormatted...</summary>
    public static void Store(IDictionary<string, object?> args, SubathonTimer t)
    {
        args["subathonTimer"] = t.Name;
        args["subathonState"] = t.State.ToString();
        args["subathonRemaining"] = Math.Round(t.RemainingSeconds);
        args["subathonFormatted"] = SubathonService.Format(t.RemainingSeconds);
        args["subathonElapsed"] = Math.Round(t.ElapsedSeconds);
        args["subathonFormattedElapsed"] = SubathonService.Format(t.ElapsedSeconds, roundUp: false);
        args["subathonAdded"] = Math.Round(t.AddedSeconds);
        args["subathonMultiplier"] = t.Multiplier;
    }

    /// <summary>A goal's state in arguments: goalName, goalPoints, goalNextStep, goalPointsToNext, goalStepsReached...</summary>
    public static void Store(IDictionary<string, object?> args, GoalLadder g)
    {
        var a = SubathonService.GoalArgs(g);
        args["goalName"] = g.Name;
        args["goalPoints"] = g.Points;
        args["goalNextStep"] = a["nextStep"];
        args["goalNextStepPoints"] = a["nextStepPoints"];
        args["goalPointsToNext"] = a["pointsToNext"];
        args["goalStepsReached"] = a["stepsReached"];
        args["goalStepCount"] = a["stepCount"];
        args["goalMultiplier"] = g.Multiplier;
        args["goalSteps"] = string.Join(", ", g.Steps.Select(s => $"{s.Points.ToString("0.##", CultureInfo.InvariantCulture)}: {s.Name}"));
    }
}

public sealed class SubathonControlSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "subathon.control";
    public string Category => SubathonFeatures.TimerCategory;
    public string DisplayName => "Start, pause or reset a subathon timer";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Timer(s),
        new("mode", "Do", ParameterKind.Choice) { DefaultValue = "Start", Choices = ["Start", "Pause", "Resume", "Pause or resume", "Reset"] },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var timer = SubathonParams.Clean(p["timer"]);
        return SubathonParams.Do(() =>
        {
            switch (p["mode"])
            {
                case "Pause": s.Pause(timer); break;
                case "Resume": s.Resume(timer); break;
                case "Pause or resume": s.Toggle(timer); break;
                case "Reset": s.Reset(timer); break;
                default: s.Start(timer); break;
            }
        });
    }
}

public sealed class SubathonAddTimeSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "subathon.addTime";
    public string Category => SubathonFeatures.TimerCategory;
    public string DisplayName => "Add time to a subathon timer";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Timer(s),
        new("seconds", "Seconds", ParameterKind.Number) { DefaultValue = "60", Help = "A negative number takes time away" },
        new("useMultiplier", "Use the timer's multiplier", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => context.Args["secondsAdded"] = s.AddTime(SubathonParams.Clean(p["timer"]), p.GetDouble("seconds"),
            p.GetBool("useMultiplier"), context.Args.GetValueOrDefault("user") as string ?? "", context.Action.Name));
}

public sealed class SubathonSetTimeSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "subathon.setTime";
    public string Category => SubathonFeatures.TimerCategory;
    public string DisplayName => "Set a subathon timer's time left";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Timer(s),
        new("seconds", "Seconds left", ParameterKind.Number) { DefaultValue = "3600" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => s.SetTime(SubathonParams.Clean(p["timer"]), p.GetDouble("seconds")));
}

public sealed class SubathonMultiplierSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "subathon.multiplier";
    public string Category => SubathonFeatures.TimerCategory;
    public string DisplayName => "Set a subathon timer's multiplier";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Timer(s),
        new("multiplier", "Multiplier", ParameterKind.Number) { DefaultValue = "2", Help = "2 = double time (happy hour), 0.5 = half, 1 = normal" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => s.SetTimerMultiplier(SubathonParams.Clean(p["timer"]), p.GetDouble("multiplier", 1)));
}

public sealed class SubathonGetSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "subathon.get";
    public string Category => SubathonFeatures.TimerCategory;
    public string DisplayName => "Get a subathon timer";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [SubathonParams.Timer(s)];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var timer = SubathonParams.Clean(p["timer"]);
        var t = s.Timer(timer) ?? throw new InvalidOperationException(timer.Length == 0 ? "There is no subathon timer yet" : $"No subathon timer named '{timer}'");
        SubathonParams.Store(context.Args, t);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class GoalAddPointsSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "goals.addPoints";
    public string Category => SubathonFeatures.GoalCategory;
    public string DisplayName => "Add goal points";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Goal(s),
        new("points", "Points", ParameterKind.Number) { DefaultValue = "1", Help = "A negative number takes points away" },
        new("useMultiplier", "Use the goal's multiplier", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => context.Args["pointsAdded"] = s.AddPoints(SubathonParams.Clean(p["goal"]), p.GetDouble("points"),
            p.GetBool("useMultiplier"), context.Args.GetValueOrDefault("user") as string ?? "", context.Action.Name));
}

public sealed class GoalSetPointsSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "goals.setPoints";
    public string Category => SubathonFeatures.GoalCategory;
    public string DisplayName => "Set goal points";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Goal(s),
        new("points", "Points", ParameterKind.Number) { DefaultValue = "0" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => s.SetPoints(SubathonParams.Clean(p["goal"]), p.GetDouble("points")));
}

public sealed class GoalResetSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "goals.reset";
    public string Category => SubathonFeatures.GoalCategory;
    public string DisplayName => "Reset a goal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [SubathonParams.Goal(s)];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => s.ResetGoal(SubathonParams.Clean(p["goal"])));
}

public sealed class GoalMultiplierSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "goals.multiplier";
    public string Category => SubathonFeatures.GoalCategory;
    public string DisplayName => "Set a goal's multiplier";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SubathonParams.Goal(s),
        new("multiplier", "Multiplier", ParameterKind.Number) { DefaultValue = "2", Help = "2 = double points, 0.5 = half, 1 = normal" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        SubathonParams.Do(() => s.SetGoalMultiplier(SubathonParams.Clean(p["goal"]), p.GetDouble("multiplier", 1)));
}

public sealed class GoalGetSubAction(SubathonService s) : ISubActionHandler
{
    public string TypeId => "goals.get";
    public string Category => SubathonFeatures.GoalCategory;
    public string DisplayName => "Get a goal";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [SubathonParams.Goal(s)];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var goal = SubathonParams.Clean(p["goal"]);
        var g = s.Goal(goal) ?? throw new InvalidOperationException(goal.Length == 0 ? "There is no goal yet" : $"No goal named '{goal}'");
        SubathonParams.Store(context.Args, g);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Timer started/paused/resumed/ended, time added; goal points added, step reached, completed.</summary>
internal sealed class SubathonTriggers(SubathonService s) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Timer(SubathonEvents.Started, "Timer started"),
        Timer(SubathonEvents.Paused, "Timer paused"),
        Timer(SubathonEvents.Resumed, "Timer resumed"),
        Timer(SubathonEvents.Ended, "Timer ended"),
        Timer(SubathonEvents.TimeAdded, "Time added"),
        Goal(SubathonEvents.PointsAdded, "Goal points added"),
        Goal(SubathonEvents.StepReached, "Goal step reached"),
        Goal(SubathonEvents.GoalCompleted, "Goal completed"),
    ];

    private EventTriggerType Timer(string type, string name) =>
        new(type, SubathonFeatures.TimerCategory, name, type,
            [new ParameterDescriptor("timer", "Timer") { Help = "Leave empty for every timer", Suggestions = () => s.Timers().Select(t => t.Name) }],
            (e, p, _) => Matches(p["timer"], e.Args.GetString("timer")));

    private EventTriggerType Goal(string type, string name) =>
        new(type, SubathonFeatures.GoalCategory, name, type,
            [new ParameterDescriptor("goal", "Goal") { Help = "Leave empty for every goal", Suggestions = () => s.Goals().Select(g => g.Name) }],
            (e, p, _) => Matches(p["goal"], e.Args.GetString("goal")));

    private static bool Matches(string wanted, string actual) =>
        wanted.Trim().Length == 0 || string.Equals(wanted.Trim(), actual, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Script functions: subathon.* for timers, goals.* for goal ladders.</summary>
public sealed class SubathonScriptFunctions(SubathonService s) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("subathon.get", "A timer ({id,name,state,remainingSeconds,elapsedSeconds,addedSeconds,multiplier,...}); timer = name, empty for the first",
            ["timer"], (_, a, _) => Task.FromResult<object?>(s.Timer(a.GetString("timer")) is { } t ? Serialize(t) : null)),
        new("subathon.list", "Every timer", [], (_, _, _) => Task.FromResult<object?>(Serialize(s.Timers()))),
        new("subathon.start", "Start a timer (or resume a paused one)", ["timer"], (_, a, _) => Run(() => s.Start(a.GetString("timer")))),
        new("subathon.pause", "Pause a running timer", ["timer"], (_, a, _) => Run(() => s.Pause(a.GetString("timer")))),
        new("subathon.resume", "Resume a paused timer", ["timer"], (_, a, _) => Run(() => s.Resume(a.GetString("timer")))),
        new("subathon.reset", "Stop a timer and put back its starting length", ["timer"], (_, a, _) => Run(() => s.Reset(a.GetString("timer")))),
        new("subathon.addTime", "Add seconds (negative takes time away); returns the seconds added", ["timer", "seconds", "useMultiplier"],
            (ctx, a, _) => Value(() => s.AddTime(a.GetString("timer"), Num(a, "seconds"), a.GetBool("useMultiplier"),
                ctx.Args.GetValueOrDefault("user") as string ?? "", "Script"))),
        new("subathon.setTime", "Set the seconds left", ["timer", "seconds"], (_, a, _) => Run(() => s.SetTime(a.GetString("timer"), Num(a, "seconds")))),
        new("subathon.setMultiplier", "Set the multiplier for event time (2 = double)", ["timer", "multiplier"],
            (_, a, _) => Run(() => s.SetTimerMultiplier(a.GetString("timer"), Num(a, "multiplier", 1)))),
        new("goals.get", "A goal ({id,name,points,steps:[{name,points}],reachedThrough,multiplier,...}); goal = name, empty for the first",
            ["goal"], (_, a, _) => Task.FromResult<object?>(s.Goal(a.GetString("goal")) is { } g ? Serialize(g) : null)),
        new("goals.list", "Every goal", [], (_, _, _) => Task.FromResult<object?>(Serialize(s.Goals()))),
        new("goals.addPoints", "Add points (negative takes away); returns the points added", ["goal", "points", "useMultiplier"],
            (ctx, a, _) => Value(() => s.AddPoints(a.GetString("goal"), Num(a, "points"), a.GetBool("useMultiplier"),
                ctx.Args.GetValueOrDefault("user") as string ?? "", "Script"))),
        new("goals.setPoints", "Set the points", ["goal", "points"], (_, a, _) => Run(() => s.SetPoints(a.GetString("goal"), Num(a, "points")))),
        new("goals.reset", "Back to 0 points, every step can be reached again", ["goal"], (_, a, _) => Run(() => s.ResetGoal(a.GetString("goal")))),
        new("goals.setMultiplier", "Set the multiplier for event points (2 = double)", ["goal", "multiplier"],
            (_, a, _) => Run(() => s.SetGoalMultiplier(a.GetString("goal"), Num(a, "multiplier", 1)))),
    ];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static JsonElement Serialize(object value) => JsonSerializer.SerializeToElement(value, Options);

    private static double Num(JsonElement a, string key, double fallback = 0) =>
        double.TryParse(a.GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static Task<object?> Run(Action action) => Value(() =>
    {
        action();
        return true;
    });

    private static Task<object?> Value<T>(Func<T> action)
    {
        try
        {
            return Task.FromResult<object?>(action());
        }
        catch (ArgumentException ex)
        {
            throw new ScriptException(ex.Message);
        }
    }
}

/// <summary>
/// Timers and goals in overlay session data, as <c>cuebot-subathon</c>: <c>{timers: [{id, name, state, remaining,
/// endsAt (Unix ms, while running), elapsed, added, multiplier}], goals: [{id, name, points, multiplier, steps:
/// [{name, points, reached}], next: {name, points} | null, previous (the last reached step's points)}]}</c>.
/// Pages count a running timer down themselves from <c>endsAt</c>.
/// </summary>
public sealed class SubathonSessionData : ISessionDataSource
{
    public const string Key = "cuebot-subathon";
    private readonly SubathonService _service;
    private readonly TimeProvider _time;

    public SubathonSessionData(SubathonService service, TimeProvider time)
    {
        _service = service;
        _time = time;
        service.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public void AddTo(JsonObject data)
    {
        var timers = new JsonArray();
        var now = _time.GetUtcNow();
        foreach (var t in _service.Timers())
        {
            timers.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["name"] = t.Name,
                ["state"] = t.State.ToString().ToLowerInvariant(),
                ["remaining"] = Math.Round(t.RemainingSeconds, 1),
                ["endsAt"] = t.State == TimerState.Running ? now.AddSeconds(t.RemainingSeconds).ToUnixTimeMilliseconds() : null,
                ["elapsed"] = Math.Round(t.ElapsedSeconds),
                ["added"] = Math.Round(t.AddedSeconds, 1),
                ["multiplier"] = t.Multiplier,
            });
        }
        var goals = new JsonArray();
        foreach (var g in _service.Goals())
        {
            var next = g.Steps.FirstOrDefault(s => s.Points > g.Points);
            var previous = g.Steps.LastOrDefault(s => s.Points <= g.Points)?.Points ?? 0;
            goals.Add(new JsonObject
            {
                ["id"] = g.Id,
                ["name"] = g.Name,
                ["points"] = g.Points,
                ["multiplier"] = g.Multiplier,
                ["steps"] = new JsonArray([.. g.Steps.Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name,
                    ["points"] = s.Points,
                    ["reached"] = g.ReachedThrough is { } r && s.Points <= r || s.Points <= g.Points,
                })]),
                ["next"] = next is null ? null : new JsonObject { ["name"] = next.Name, ["points"] = next.Points },
                ["previous"] = previous,
            });
        }
        data[Key] = new JsonObject { ["timers"] = timers, ["goals"] = goals };
    }
}
