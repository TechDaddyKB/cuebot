using System.Diagnostics;
using Avalonia;
using Cuebot.App.Platform;
using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.App;

internal static class Program
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The display backend in use, logged at startup.</summary>
    public static string BackendName { get; private set; } = "default";

    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        if (options.ShowHelp || options.Errors.Count > 0)
        {
            ConsoleAttach.Attach();
            foreach (var error in options.Errors) Console.Error.WriteLine(error);
            Console.WriteLine(StartupOptions.HelpText);
            return options.Errors.Count > 0 ? 2 : 0;
        }
        if (options.Headless) ConsoleAttach.Attach();

        // --data wins over CUEBOT_DATA; exporting it keeps child processes and relaunches on the same folder.
        if (options.DataFolder is not null) Environment.SetEnvironmentVariable("CUEBOT_DATA", options.DataFolder);
        AppPaths paths;
        try
        {
            paths = new AppPaths(options.ResolveDataFolder(Environment.GetEnvironmentVariable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Cannot use the data folder: {ex.Message}");
            return 1;
        }

        // AI clients spawn "Cuebot --mcp-stdio"; it only bridges stdio to the running instance's MCP endpoint.
        if (Cuebot.Server.Mcp.McpStdioBridge.IsRequested(args))
            return Cuebot.Server.Mcp.McpStdioBridge.Run(paths.Root);

        // One instance per data folder: a second launch brings the first one forward and exits.
        var instance = SingleInstance.TryAcquire(paths.Root);
        if (instance is null)
        {
            var answered = SingleInstance.ActivateExistingAsync(paths.Root, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Console.WriteLine(answered
                ? $"Cuebot is already running with {paths.Root}; brought it to the front."
                : $"Cuebot is already running with {paths.Root} but did not respond.");
            return answered ? 0 : 1;
        }

        var ctx = Prepare(options, paths, instance);
        // Crash capture: unhandled exceptions are saved to crashes/crash-*.json (never sent); notes a previous crash.
        Cuebot.App.Diagnostics.CrashCapture.Install(ctx);
        try
        {
            if (options.Headless)
            {
                BackendName = "none (headless)";
                return HeadlessRunner.Run(ctx);
            }
            return RunDesktop(args, ctx);
        }
        finally
        {
            instance.Dispose();
            if (ctx.RestartRequested) Restart(args);
        }
    }

    /// <summary>Applies a scheduled restore, the window reset and the start-up backup, before any store opens.</summary>
    private static ShellContext Prepare(StartupOptions options, AppPaths paths, SingleInstance instance)
    {
        var notes = new List<(LogLevel, string)>();
        var settings = new AppSettingsStore(paths);
        if (settings.LoadError is { } loadError) notes.Add((LogLevel.Warning, loadError));
        var backups = new BackupService(paths, () => settings.Settings.Backups.Keep);
        var relaunched = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(LinuxBackend.FallbackMarker));

        try
        {
            if (backups.ApplyPendingRestore() is { } safety)
            {
                notes.Add((LogLevel.Info, $"Restored a backup; the previous data was saved as {safety.Name}"));
                settings = new AppSettingsStore(paths);   // app.json came from the backup too
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            notes.Add((LogLevel.Error, $"Restoring the backup failed: {ex.Message}"));
        }

        if (options.ResetWindow && settings.Settings.Window is not null)
        {
            settings.Settings.Window = null;
            settings.TrySave();
            notes.Add((LogLevel.Info, "Window position reset (--reset-window)"));
        }

        if (settings.Settings.Backups.OnStart && !relaunched && backups.HasData)
        {
            try
            {
                var backup = backups.Create("startup");
                notes.Add((LogLevel.Info, $"Backup created: {backup.Name}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                notes.Add((LogLevel.Error, $"Start-up backup failed: {ex.Message}"));
            }
        }

        var ctx = new ShellContext { Options = options, Paths = paths, Settings = settings, Instance = instance };
        foreach (var (level, message) in notes) ctx.Note(level, message);
        return ctx;
    }

    private static int RunDesktop(string[] args, ShellContext ctx)
    {
        var backend = OperatingSystem.IsLinux()
            ? LinuxBackend.Choose(args, Environment.GetEnvironmentVariable)
            : DisplayBackend.Default;
        ShellContext.Current = ctx;
        if (ctx.Settings.Settings.General.ShowTrayIcon) ctx.TrayProbe = TrayProbe.IsTrayAvailableAsync(TimeSpan.FromSeconds(2));
        int exitCode;
        try
        {
            exitCode = BuildAvaloniaApp(backend).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (backend == DisplayBackend.Wayland && LinuxBackend.CanFallBack(args, Environment.GetEnvironmentVariable))
        {
            // The Wayland backend is still young; if it cannot start, relaunch once on X11 (XWayland).
            Console.Error.WriteLine($"Wayland backend failed to start ({ex.Message}); relaunching with X11.");
            ctx.Host?.ShutDown(ShutdownTimeout);
            ctx.Instance?.Dispose();   // the relaunched process must be able to claim the data folder
            return RelaunchOnX11(args);
        }

        // Dispose once, off the UI thread (Exit already started it; this waits for it to finish).
        if (ctx.Host is { } host && !host.ShutDown(ShutdownTimeout))
            Console.Error.WriteLine("Shutdown timed out; some services may not have stopped cleanly.");
        return exitCode;
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(DisplayBackend.Default);

    private static AppBuilder BuildAvaloniaApp(DisplayBackend backend)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => Cuebot.App.Diagnostics.CrashCapture.InstallUiThreadHandler());   // UI-thread crashes too
        BackendName = OperatingSystem.IsLinux() ? backend.ToString() : "native";
        return backend == DisplayBackend.Wayland ? builder.UseWayland() : builder;
    }

    private static int RelaunchOnX11(string[] args)
    {
        var info = SelfStartInfo(args, preferAppImage: false);   // this process stays alive, and so does its mount
        info.Environment[LinuxBackend.FallbackMarker] = "1";
        using var child = Process.Start(info)!;
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>Starts a new Cuebot with the same arguments (minus one-shot switches) after this one has exited.</summary>
    private static void Restart(string[] args)
    {
        var kept = args.Where(a => !a.Equals("--reset-window", StringComparison.OrdinalIgnoreCase)
                                   && !a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)).ToArray();
        try
        {
            Process.Start(SelfStartInfo(kept, preferAppImage: true))?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not restart Cuebot: {ex.Message}");
        }
    }

    /// <summary>How to start this program again: the AppImage itself, the apphost, or <c>dotnet Cuebot.dll</c>.</summary>
    private static ProcessStartInfo SelfStartInfo(IEnumerable<string> args, bool preferAppImage)
    {
        var appImage = preferAppImage ? Environment.GetEnvironmentVariable("APPIMAGE") : null;
        var path = !string.IsNullOrEmpty(appImage) && File.Exists(appImage) ? appImage : Environment.ProcessPath!;
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
}
