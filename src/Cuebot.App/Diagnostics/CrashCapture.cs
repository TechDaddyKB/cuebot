using Avalonia.Threading;
using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.App.Diagnostics;

/// <summary>
/// Global exception handlers (installed by <see cref="Program"/>): an unhandled exception is written to
/// <c>crashes/crash-*.json</c> before the process ends as it would anyway. Nothing is sent anywhere; on the next
/// start the UI offers to report it (see <c>CrashBannerViewModel</c>) and headless mode logs the file's path.
/// </summary>
public static class CrashCapture
{
    private static ShellContext? _ctx;
    private static int _written;

    /// <summary>The crash file written by this process, if any.</summary>
    public static string? WrittenPath { get; private set; }

    /// <summary>The last unobserved task exception (not fatal; kept for problem reports).</summary>
    public static Exception? LastUnobservedTaskException { get; private set; }

    public static void Install(ShellContext ctx)
    {
        _ctx = ctx;
        if (new CrashStore(ctx.Paths).LatestUnseen() is { } previous)
            ctx.Note(LogLevel.Warning, $"Cuebot closed unexpectedly last time; the crash details are in {previous}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(ex, "AppDomain", e.IsTerminating);
        };
        // Unobserved task exceptions never ended the process and still do not: they are logged, not written as crashes.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LastUnobservedTaskException = e.Exception;
            TryLog(log => log.Error("Cuebot", "Unobserved background task failure", e.Exception.InnerException ?? e.Exception));
        };
    }

    /// <summary>Called once Avalonia is set up (the UI dispatcher must not be touched before that).</summary>
    public static void InstallUiThreadHandler()
    {
        // Not marked handled: the exception still ends the app as before; we only record it first.
        Dispatcher.UIThread.UnhandledException += (_, e) => Write(e.Exception, "UI thread", terminating: true);
    }

    /// <summary>Writes a crash file once per process (both handlers may see the same exception). Never throws.</summary>
    public static string? Write(Exception ex, string source, bool terminating)
    {
        if (_ctx is not { } ctx || Interlocked.Exchange(ref _written, 1) == 1) return null;
        try
        {
            var path = WriteCrash(ctx.Paths, ex, source, terminating, Connections(ctx), DateTimeOffset.Now, Program.BackendName);
            WrittenPath = path;
            try { Console.Error.WriteLine($"Cuebot crashed; details were saved to {path}"); } catch (IOException) { }
            return path;
        }
        catch
        {
            return null;   // a crash handler must never throw
        }
    }

    /// <summary>Builds and saves the crash record (also used by tests).</summary>
    public static string WriteCrash(AppPaths paths, Exception ex, string source, bool terminating,
        IReadOnlyList<ConnectionInfo> connections, DateTimeOffset time, string displayBackend)
    {
        var record = new CrashRecord
        {
            Time = time,
            Source = source,
            Terminating = terminating,
            Exception = ExceptionInfo.From(ex),
            Environment = EnvironmentInfo.Collect(displayBackend),
            Connections = connections.ToList(),
            Log = LogTail.Read(paths.Logs).Select(l => SecretScrubber.ScrubText(l)).ToList(),
        };
        return new CrashStore(paths).Write(record);
    }

    private static IReadOnlyList<ConnectionInfo> Connections(ShellContext ctx)
    {
        try
        {
            return ctx.Host?.GetAll<IConnectionStatus>().Select(c => new ConnectionInfo(c.Name, c.State.ToString(), "")).ToArray() ?? [];
        }
        catch
        {
            return [];   // the host may be half disposed
        }
    }

    private static void TryLog(Action<AppLog> write)
    {
        try
        {
            if (_ctx?.Host is { } host) write(host.Get<AppLog>());
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
