using Cuebot.Core.Storage;
namespace Cuebot.StreamApps;

/// <summary>Lumia Stream Developer API settings.</summary>
public sealed class LumiaSettings
{
    /// <summary>The PC running Lumia Stream (its API listens on this PC only by default).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Lumia's local API port (39231 in its documentation).</summary>
    public int Port { get; set; } = 39231;

    /// <summary>The token from Lumia's Settings → API ("Enable Developers API", "Copy Token"). Never logged.</summary>
    [Secret]
    public string Token { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when Lumia closes or is not running yet.</summary>
    public bool AutoReconnect { get; set; } = true;
}
