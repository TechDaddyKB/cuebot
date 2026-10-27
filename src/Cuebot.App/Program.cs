using System.Diagnostics;
using Avalonia;
using Cuebot.App.Platform;

namespace Cuebot.App;

internal static class Program
{
    /// <summary>The display backend in use, logged at startup.</summary>
    public static string BackendName { get; private set; } = "default";

    [STAThread]
    public static int Main(string[] args)
    {
        var backend = OperatingSystem.IsLinux()
            ? LinuxBackend.Choose(args, Environment.GetEnvironmentVariable)
            : DisplayBackend.Default;
        try
        {
            return BuildAvaloniaApp(backend).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (backend == DisplayBackend.Wayland && LinuxBackend.CanFallBack(args, Environment.GetEnvironmentVariable))
        {
            // The Wayland backend is still young; if it cannot start, relaunch once on X11 (XWayland).
            Console.Error.WriteLine($"Wayland backend failed to start ({ex.Message}); relaunching with X11.");
            return RelaunchOnX11(args);
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(DisplayBackend.Default);

    private static AppBuilder BuildAvaloniaApp(DisplayBackend backend)
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        BackendName = OperatingSystem.IsLinux() ? backend.ToString() : "native";
        return backend == DisplayBackend.Wayland ? builder.UseWayland() : builder;
    }

    private static int RelaunchOnX11(string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment[LinuxBackend.FallbackMarker] = "1";
        using var child = Process.Start(info)!;
        child.WaitForExit();
        return child.ExitCode;
    }
}
