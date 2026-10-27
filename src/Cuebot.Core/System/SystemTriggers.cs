using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Core.SystemTools;

/// <summary>Triggers for file/folder watchers, file tails and process watchers, filtered by watcher name.</summary>
public sealed class SystemTriggers(WatcherService watchers) : ITriggerProvider
{
    private const string Category = "System";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        var files = WatcherParameter(s => s.FileWatchers);
        var tails = WatcherParameter(s => s.FileTails);
        var processes = WatcherParameter(s => s.ProcessWatchers);
        return
        [
            new EventTriggerType(WatcherEvents.FileCreated, Category, "File created", WatcherEvents.FileCreated, [files], MatchesWatcher),
            new EventTriggerType(WatcherEvents.FileChanged, Category, "File changed", WatcherEvents.FileChanged, [files], MatchesWatcher),
            new EventTriggerType(WatcherEvents.FileDeleted, Category, "File deleted", WatcherEvents.FileDeleted, [files], MatchesWatcher),
            new EventTriggerType(WatcherEvents.FileRenamed, Category, "File renamed", WatcherEvents.FileRenamed, [files], MatchesWatcher),
            new EventTriggerType(WatcherEvents.FileTailLine, Category, "File tail line", WatcherEvents.FileTailLine, [tails], MatchesWatcher),
            new EventTriggerType(WatcherEvents.ProcessStarted, Category, "Process started", WatcherEvents.ProcessStarted, [processes], MatchesWatcher),
            new EventTriggerType(WatcherEvents.ProcessStopped, Category, "Process stopped", WatcherEvents.ProcessStopped, [processes], MatchesWatcher),
        ];
    }

    private ParameterDescriptor WatcherParameter(Func<WatcherSettings, IEnumerable<WatcherDefinition>> scope) =>
        new("watcher", "Watcher") { Help = "Empty matches every watcher", Suggestions = () => watchers.Names(scope) };

    /// <summary>Empty filter matches all; otherwise the watcher's name or id must match.</summary>
    public static bool MatchesWatcher(BotEvent e, ParameterValues settings, IDictionary<string, object?> args)
    {
        var wanted = settings["watcher"].Trim();
        if (wanted.Length == 0) return true;
        return string.Equals(e.Args.GetValueOrDefault("watcherName") as string, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Args.GetValueOrDefault("watcherId") as string, wanted, StringComparison.OrdinalIgnoreCase);
    }
}
