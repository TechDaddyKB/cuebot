using System.Diagnostics;

namespace Cuebot.Elgato.Studio;

/// <summary>
/// Whether Elgato's desktop apps are running on this PC. Elgato Studio and Camera Hub publish no control API (see
/// PROTOCOL.md), so this is all Cuebot can report about them; it is shown on the Elgato tab and not as a connection.
/// </summary>
public static class ElgatoAppPresence
{
    public static readonly string[] StudioProcesses = ["Elgato Studio", "ElgatoStudio"];
    public static readonly string[] CameraHubProcesses = ["Camera Hub", "CameraHub"];
    public static readonly string[] ControlCenterProcesses = ["ControlCenter", "Control Center", "Elgato Control Center"];

    public static bool IsRunning(IReadOnlyList<string> processNames)
    {
        foreach (var name in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            var found = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (found) return true;
        }
        return false;
    }

    public const string StudioUnavailable =
        "Elgato Studio has no public control API: Elgato documents only its Stream Deck plugin, and Cuebot does not reverse-engineer private interfaces. Recording, preview and snapshot triggers and sub-actions are not available yet.";

    public const string CameraHubPrompterUnavailable =
        "Camera Hub's Prompter and EpocCam settings live inside the Camera Hub app, which has no public control API, so they cannot be controlled yet.";
}
