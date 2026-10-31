using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

public static class CoreEvents
{
    public const string AppStarted = "core.appStarted";
}

public sealed class CoreTriggers(ProfileService profile) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("core.appStarted", Categories.Core, "Application started", CoreEvents.AppStarted),
        new EventTriggerType("core.timer", Categories.Core, "Timer", TimerService.EventType,
            [new ParameterDescriptor("timer", "Timer") { Suggestions = () => profile.Snapshot.Timers.Select(t => t.Name) }],
            // An empty Timer field means every timer, like the other trigger filters.
            (e, settings, _) => settings["timer"].Trim().Length == 0
                || string.Equals(e.Args.GetValueOrDefault("timerName") as string, settings["timer"].Trim(), StringComparison.OrdinalIgnoreCase)),
    ];
}
