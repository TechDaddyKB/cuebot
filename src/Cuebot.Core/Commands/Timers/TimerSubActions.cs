using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Commands;

/// <summary>Enables, disables or toggles a timer and/or changes its interval (runtime only).</summary>
public sealed class SetTimerStateSubAction(TimerService timers, ProfileService profile) : ISubActionHandler
{
    public string TypeId => "timers.setState";
    public string Category => "Timers";
    public string DisplayName => "Set timer state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("timer", "Timer") { Suggestions = () => profile.Snapshot.Timers.Select(t => t.Name) },
        new("state", "State", ParameterKind.Choice) { DefaultValue = "Enable", Choices = ["No change", .. CommandCategories.States] },
        new("interval", "Interval (seconds)") { Help = "Leave empty to keep the current interval" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["timer"];
        var state = timers.GetState(name) ?? throw new InvalidOperationException($"No timer named '{name}'");
        bool? enabled = p["state"] == "No change" ? null : CommandCategories.Apply(p["state"], state.Enabled);
        int? interval = null;
        if (p["interval"].Trim().Length > 0)
        {
            interval = int.TryParse(p["interval"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
                ? v
                : throw new ArgumentException($"Interval '{p["interval"]}' is not a whole number of seconds");
        }
        timers.SetState(state.Id.ToString(), enabled, interval);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Writes a timer's effective state into arguments.</summary>
public sealed class GetTimerStateSubAction(TimerService timers, ProfileService profile, TimeProvider? time = null) : ISubActionHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string TypeId => "timers.getState";
    public string Category => "Timers";
    public string DisplayName => "Get timer state";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("timer", "Timer") { DefaultValue = "%timerName%", Suggestions = () => profile.Snapshot.Timers.Select(t => t.Name) },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var state = timers.GetState(p["timer"]) ?? throw new InvalidOperationException($"No timer named '{p["timer"]}'");
        context.Args["timerStateName"] = state.Name;
        context.Args["timerStateId"] = state.Id.ToString();
        context.Args["timerEnabled"] = state.Enabled;
        context.Args["timerInterval"] = state.IntervalSeconds;
        context.Args["timerRepeat"] = state.Repeat;
        context.Args["timerChatLines"] = state.ChatLines;
        context.Args["timerNextFire"] = state.NextFire?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        context.Args["timerSecondsRemaining"] = state.NextFire is { } next
            ? Math.Max(0, (int)Math.Ceiling((next - _time.GetUtcNow()).TotalSeconds))
            : -1;
        return Task.FromResult(SubActionResult.Next);
    }
}
