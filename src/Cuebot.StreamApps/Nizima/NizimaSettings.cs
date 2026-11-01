namespace Cuebot.StreamApps;

/// <summary>nizima LIVE connection settings (its Plugin API; the token comes from registering, no password).</summary>
public sealed class NizimaSettings
{
    /// <summary>nizima LIVE only accepts plug-ins on its own PC.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>The port shown in nizima LIVE's Plugin Manager (22022 unless it had to pick another).</summary>
    public int Port { get; set; } = 22022;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when nizima LIVE closes or its plug-in function is off.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>What nizima LIVE handed out when Cuebot registered; used to reconnect without registering again.</summary>
    public string Token { get; set; } = "";
}
