namespace Cuebot.StreamApps;

/// <summary>Persisted stream-app configuration (streamapps.json). Neither app uses credentials.</summary>
public sealed class StreamAppsSettings
{
    public MeldStudioSettings Meld { get; set; } = new();
    public PolyPopSettings PolyPop { get; set; } = new();
    public TitsSettings Tits { get; set; } = new();
    public LumiaSettings Lumia { get; set; } = new();
    public ObsbotSettings Obsbot { get; set; } = new();
    public MtionSettings Mtion { get; set; } = new();
}

public sealed class MeldStudioSettings
{
    /// <summary>Shown as <c>meldStudio.name</c> in trigger arguments.</summary>
    public string Name { get; set; } = "Meld Studio";

    /// <summary>Stable id of this connection (<c>meldStudio.id</c>); created on first use.</summary>
    public string ConnectionId { get; set; } = "";

    /// <summary>The PC running Meld Studio. Meld listens on all interfaces once "Allow remote connections" is on.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Meld Studio's WebChannel port: fixed at 13376 today ("may be configurable in the future").</summary>
    public int Port { get; set; } = 13376;

    public bool AutoConnect { get; set; }

    /// <summary>Reconnect (with backoff) when Meld closes or is not running yet.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Register for <c>gainUpdated</c> on every track (fast mute/fader updates; "Track gain changed" events).</summary>
    public bool ObserveTrackGain { get; set; } = true;
}

/// <summary>How Cuebot talks to PolyPop's community WebSocket plugin.</summary>
public enum PolyPopMode
{
    /// <summary>Cuebot listens; the plugin (a websocket client) connects to it. This is how the published plugin works.</summary>
    Server,

    /// <summary>Cuebot connects to a websocket server at Address:Port/Endpoint (for plugin builds that listen instead).</summary>
    Client,
}

public sealed class PolyPopSettings
{
    public PolyPopMode Mode { get; set; } = PolyPopMode.Server;

    /// <summary>Server: the address to listen on (127.0.0.1 = this PC only). Client: the host to connect to.</summary>
    public string Address { get; set; } = "127.0.0.1";

    /// <summary>9652 as in Streamer.bot (set the same port in the PolyPop plugin; the plugin's own default is 38031).</summary>
    public int Port { get; set; } = 9652;

    public string Endpoint { get; set; } = "/";

    /// <summary>Start listening (or connecting) when Cuebot starts.</summary>
    public bool AutoStart { get; set; }
}
