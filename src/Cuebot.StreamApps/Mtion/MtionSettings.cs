namespace Cuebot.StreamApps;

/// <summary>mtion studio's local developer API (streamapps.json → Mtion). The API has no credentials.</summary>
public sealed class MtionSettings
{
    /// <summary>
    /// The PC running mtion studio. "localhost" as in mtion's docs (and Mix It Up): if mtion's listener checks the
    /// Host header, "127.0.0.1" could be refused.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>35393, the port mtion's docs give (they do not say whether it can be changed).</summary>
    public int Port { get; set; } = 35393;

    /// <summary>Check for mtion studio when Cuebot starts and keep watching it.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Seconds between checks for mtion studio and its open clubhouse.</summary>
    public int PollSeconds { get; set; } = 5;
}
