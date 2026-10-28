using System.Runtime.InteropServices;
using Cuebot.Core.Logging;

namespace Cuebot.App.Shell;

/// <summary>
/// <c>--headless</c>: no window and no display server needed. Every service starts as usual, the log goes to the
/// console as well as the log file, and Ctrl+C / SIGTERM / SIGHUP (or closing the console on Windows) stop it cleanly.
/// </summary>
public static class HeadlessRunner
{
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    /// <param name="ctx">Prepared startup state.</param>
    /// <param name="stop">Completes the run when cancelled (tests); signals are always honoured too.</param>
    public static int Run(ShellContext ctx, CancellationToken stop = default)
    {
        var host = AppHost.Create(ctx.Paths, ctx.Settings);
        ctx.Host = host;
        var log = host.Get<AppLog>();
        var console = TextWriter.Synchronized(Console.Out);
        log.EntryAdded += entry =>
        {
            try { console.WriteLine(entry.ToString()); }
            catch (IOException) { }   // the terminal went away; the file log continues
        };
        ctx.FlushNotes(log);

        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Register(() => stopped.TrySetResult("stop requested"));
        var signals = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT, PosixSignal.SIGHUP })
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(signal, c =>
                {
                    c.Cancel = true;   // shut down ourselves, flushing stores, instead of being killed
                    stopped.TrySetResult(c.Signal.ToString());
                }));
            }
            catch (PlatformNotSupportedException) { }
        }

        if (ctx.Instance is { } instance)
        {
            instance.ActivationRequested += () => log.Info("Cuebot", "Another launch asked to show the window; running headless, so it was ignored");
            instance.ShutdownRequested += () => stopped.TrySetResult("--shutdown requested");
        }

        try
        {
            log.Info("Cuebot", "Running headless; press Ctrl+C to stop");
            var start = host.StartAsync();
            var reason = stopped.Task.GetAwaiter().GetResult();
            log.Info("Cuebot", $"Stopping ({reason})");
            if (!start.Wait(TimeSpan.FromSeconds(5))) log.Warn("Cuebot", "Services were still starting; shutting down anyway");
        }
        finally
        {
            foreach (var s in signals) s.Dispose();
            if (!host.ShutDown(ShutdownTimeout)) console.WriteLine("Shutdown timed out; some services may not have stopped cleanly.");
        }
        return 0;
    }
}

/// <summary>Cuebot is a GUI (WinExe) program on Windows; headless and help output attach to the parent console.</summary>
internal static partial class ConsoleAttach
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    public static void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(StdOutputHandle);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) return;   // already redirected to a file or pipe
        if (AttachConsole(AttachParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            Console.WriteLine();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);
}
