namespace Cuebot.StreamApps;

/// <summary>mtion studio's local developer API (streamapps.json → Mtion). The API has no credentials.</summary>
public sealed class MtionSettings
{
    /// <summary>The PC running mtion studio; its API answers on localhost only.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Fixed by mtion: 35393.</summary>
    public int Port { get; set; } = 35393;

    /// <summary>Check for mtion studio when Cuebot starts and keep watching it.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Seconds between checks for mtion studio and its open clubhouse.</summary>
    public int PollSeconds { get; set; } = 5;
}
