namespace Cuebot.StreamApps;

/// <summary>VNyan connection settings. None of VNyan's ways in has a password.</summary>
public sealed class VnyanSettings
{
    /// <summary>
    /// The PC running VNyan. VNyan's docs only name http://localhost:8069; whether VNyan also answers from another
    /// PC has not been checked, so a remote host is allowed but not promised.
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>VNyan's HTTP API port (8069 unless it was changed in VNyan).</summary>
    public int Port { get; set; } = 8069;

    /// <summary>
    /// VNyan's WebSocket receiver port (Settings > Misc > Websockets, "Start Receiver" on). VNyan's docs show 8000 in
    /// their Mix It Up example (ws://localhost:8000/vnyan).
    /// </summary>
    public int WebSocketPort { get; set; } = 8000;

    /// <summary>
    /// VNyan's OSC receiver port (Settings > Misc). VNyan's docs give no default, so 0 means "not set yet" and the
    /// parameter steps ask for it.
    /// </summary>
    public int OscPort { get; set; }

    /// <summary>Run a WebSocket server on this PC for VNyan's "Websocket Message" node (the "VNyan message" trigger).</summary>
    public bool Listen { get; set; }

    /// <summary>The port of that server (ws://127.0.0.1:&lt;port&gt;/, any path).</summary>
    public int ListenPort { get; set; } = 8070;
}
