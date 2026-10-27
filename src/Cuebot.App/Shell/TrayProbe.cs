using Tmds.DBus.Protocol;

namespace Cuebot.App.Shell;

/// <summary>
/// Whether a tray icon would actually be visible. Windows always has a notification area. On Linux the icon is a
/// StatusNotifierItem, which only shows when a StatusNotifierWatcher (KDE Plasma, most wlroots bars, GNOME with the
/// AppIndicator extension, ...) owns its name on the session bus; plain GNOME and many minimal Wayland sessions have none.
/// </summary>
public static class TrayProbe
{
    private const string Watcher = "org.kde.StatusNotifierWatcher";

    public static async Task<bool> IsTrayAvailableAsync(TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return true;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return false;
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            return await HasOwnerAsync(address, Watcher).WaitAsync(cts.Token);
        }
        catch (Exception)
        {
            return false;   // no bus, sandbox without permission, timeout: treat as "no tray"
        }
    }

    private static async Task<bool> HasOwnerAsync(string address, string name)
    {
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "NameHasOwner",
                signature: "s");
            writer.WriteString(name);
            message = writer.CreateMessage();
        }
        return await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadBool(), null);
    }
}
