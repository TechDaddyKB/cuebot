using System.Collections.Concurrent;
using Cuebot.Core.Logging;

namespace Cuebot.App.Platform;

/// <summary>Warnings raised before the log exists (e.g. while settings load); <see cref="AppHost"/> logs them.</summary>
internal static class StartupWarnings
{
    private static readonly ConcurrentQueue<(string Source, string Message)> Pending = new();
    private static AppLog? _log;

    public static void Add(string source, string message)
    {
        if (_log is { } log) log.Warn(source, message);
        else Pending.Enqueue((source, message));
    }

    public static void Attach(AppLog log)
    {
        _log = log;
        while (Pending.TryDequeue(out var w)) log.Warn(w.Source, w.Message);
    }
}
