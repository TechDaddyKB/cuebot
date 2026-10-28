namespace Cuebot.StreamApps;

/// <summary>How Cuebot sends OSC to OBSBOT Center (or straight to a Tail 2 / Tail Air).</summary>
public enum ObsbotTransport { Udp, Tcp }

/// <summary>Which command family series-specific sub-actions use (OBSBOT's OSC addresses differ per camera series).</summary>
public enum ObsbotSeries { Auto, Tiny, Tail, Meet }

/// <summary>OBSBOT Center's OSC server (streamapps.json → Obsbot). No credentials: OBSBOT's OSC has no authentication.</summary>
public sealed class ObsbotSettings
{
    /// <summary>The PC running OBSBOT Center (127.0.0.1), or a Tail 2 / Tail Air's own IP address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>OBSBOT Center's OSC "receive port" (16284 in community setups; Tail hardware uses 57110).</summary>
    public int Port { get; set; } = 16284;

    public ObsbotTransport Transport { get; set; } = ObsbotTransport.Udp;

    /// <summary>UDP only: the local port OBSBOT Center sends its replies to (57120 by default).</summary>
    public int ListenPort { get; set; } = 57120;

    /// <summary>Seconds between state queries (zoom, gimbal, AI tracking, presets); 0 = never poll.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>The camera series for Tiny/Tail/Meet commands; Auto guesses from the selected device's name.</summary>
    public ObsbotSeries Series { get; set; } = ObsbotSeries.Auto;

    public bool AutoConnect { get; set; }

    public bool AutoReconnect { get; set; } = true;
}
