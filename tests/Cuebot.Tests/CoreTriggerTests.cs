using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests;

public sealed class CoreTriggerTests
{
    [Fact]
    public void Timer_trigger_with_an_empty_timer_runs_for_every_timer()
    {
        using var profile = new ProfileService(null, new AppLog());
        var trigger = new CoreTriggers(profile).GetTriggers().Single(t => t.TypeId == "core.timer");
        bool Matches(string timer, string fired) => trigger.Matches(
            new BotEvent(TimerService.EventType, new Dictionary<string, object?> { ["timerName"] = fired }), new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["timer"] = timer }), new Dictionary<string, object?>());

        Assert.True(Matches("", "Socials"));
        Assert.True(Matches("socials", "Socials"));
        Assert.False(Matches("Hydrate", "Socials"));
    }
}
