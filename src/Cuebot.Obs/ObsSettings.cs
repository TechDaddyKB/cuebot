using Cuebot.Core.Storage;
namespace Cuebot.Obs;

/// <summary>One named obs-websocket connection.</summary>
public class ObsConnectionSettings
{
    /// <summary>Shown in the UI and used by sub-actions and triggers to pick the connection.</summary>
    public string Name { get; set; } = "OBS";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;

    /// <summary>obs-websocket server password; empty when authentication is disabled in OBS.</summary>
    [Secret]
    public string Password { get; set; } = "";

    /// <summary>Connect on startup and keep reconnecting while OBS is unreachable.</summary>
    public bool AutoConnect { get; set; }
}

/// <summary>
/// Settings persisted in obs.json. The object itself is the default connection (the one used when a sub-action
/// leaves its connection empty); <see cref="Connections"/> holds any additional OBS instances.
/// </summary>
public sealed class ObsSettings : ObsConnectionSettings
{
    public List<ObsConnectionSettings> Connections { get; set; } = [];
}
