using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Core.Commands.Timers;

/// <summary>
/// Streamer.bot's "Cron" trigger: the trigger carries its own 5-field cron expression (local time), independent of
/// the Timers page. <see cref="CronClock"/> publishes one "core.cron" event per wall-clock minute; each trigger runs
/// when its expression matches that minute, with %expression% and %triggeredAt%.
/// </summary>
public sealed class CronTriggers : ITriggerProvider
{
    public const string TypeId = "core.cron";
    private readonly ConcurrentDictionary<string, CronExpression?> _cache = new(StringComparer.Ordinal);

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(TypeId, "Core", "Cron schedule", CronClock.EventType,
            [new ParameterDescriptor("expression", "Cron expression") { DefaultValue = "0 * * * *", Help = "minute hour day-of-month month day-of-week, e.g. */15 * * * *" }],
            (e, s, args) =>
            {
                if (e.Args.GetValueOrDefault("minute") is not DateTime minute) return false;
                var text = s["expression"].Trim();
                var cron = _cache.GetOrAdd(text, t => CronExpression.TryParse(t, out var c, out _) ? c : null);
                if (cron is null || !cron.Matches(minute)) return false;
                args["expression"] = text;
                args["triggeredAt"] = minute;
                return true;
            }),
    ];
}

/// <summary>
/// Publishes "core.cron" at most once per local wall-clock minute (ticked by the timer service), and only when an enabled
/// Cron trigger's expression matches that minute (so WebSocket clients see System.Cron only when a schedule fires).
/// Without a profile (tests) it publishes every minute.
/// </summary>
public sealed class CronClock(EventBus bus, ProfileService? profile = null) : IPeriodicService
{
    public const string EventType = "core.cron";
    private DateTime _last = DateTime.MinValue;

    public void Tick(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var minute = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Local);
        if (minute <= _last) return;
        var first = _last == DateTime.MinValue;
        _last = minute;
        if (first && local.Second > 5) return; // started mid-minute: wait for the next full minute
        if (profile is not null && !AnyMatches(profile, minute)) return;
        bus.Publish(new BotEvent(EventType, new Dictionary<string, object?>
        {
            ["minute"] = minute,
            ["minuteText"] = minute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        }));
    }

    private static bool AnyMatches(ProfileService profile, DateTime minute) =>
        profile.Snapshot.Actions.Where(a => a.Enabled).SelectMany(a => a.Triggers)
            .Where(t => t.Enabled && string.Equals(t.Type, CronTriggers.TypeId, StringComparison.OrdinalIgnoreCase))
            .Any(t => CronExpression.TryParse(t.Settings.GetValueOrDefault("expression") ?? "0 * * * *", out var c, out _) && c.Matches(minute));
}
