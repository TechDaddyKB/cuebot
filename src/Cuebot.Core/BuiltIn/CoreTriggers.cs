using Cuebot.Core.Actions;

namespace Cuebot.Core.BuiltIn;

public static class CoreEvents
{
    public const string AppStarted = "core.appStarted";

    /// <summary>Cuebot finished starting on a streamer profile (after <see cref="AppStarted"/>).</summary>
    public const string ProfileStarted = "core.profileStarted";

    /// <summary>Cuebot is about to switch to another profile; it waits for these actions first.</summary>
    public const string ProfileStopping = "core.profileStopping";
}

public sealed class CoreTriggers(ProfileService profile) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("core.appStarted", Categories.Core, "Application started", CoreEvents.AppStarted),
        new EventTriggerType("core.profileStarted", Categories.Core, "Profile started", CoreEvents.ProfileStarted),
        new EventTriggerType("core.profileStopping", Categories.Core, "Profile stopping", CoreEvents.ProfileStopping),
        new EventTriggerType("core.timer", Categories.Core, "Timer", TimerService.EventType,
            [new ParameterDescriptor("timer", "Timer") { Suggestions = () => profile.Snapshot.Timers.Select(t => t.Name) }],
            // An empty Timer field means every timer, like the other trigger filters.
            (e, settings, _) => settings["timer"].Trim().Length == 0
                || string.Equals(e.Args.GetValueOrDefault("timerName") as string, settings["timer"].Trim(), StringComparison.OrdinalIgnoreCase)),
    ];
}
