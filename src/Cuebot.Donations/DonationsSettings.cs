namespace Cuebot.Donations;

/// <summary>Persisted Streamlabs and StreamElements configuration (donations.json). Tokens are never logged.</summary>
public sealed class DonationsSettings
{
    public StreamlabsSettings Streamlabs { get; set; } = new();
    public StreamElementsSettings StreamElements { get; set; } = new();
}

public sealed class StreamlabsSettings
{
    /// <summary>Socket API token (Streamlabs dashboard → Settings → API Settings → API Tokens). A secret.</summary>
    public string SocketToken { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Also publish the Twitch/YouTube follows, subs, bits, raids... that Streamlabs relays. Off by default because
    /// the native platform modules already report them.
    /// </summary>
    public bool RelayPlatformEvents { get; set; }

    /// <summary>Engine.IO protocol version that last worked (4 = Socket.IO v3/v4, 3 = Socket.IO v2); tried first.</summary>
    public int EngineIoVersion { get; set; } = 4;
}

public enum StreamElementsTransport
{
    /// <summary>The current Astro gateway (wss://astro.streamelements.com).</summary>
    Astro,

    /// <summary>The older Socket.IO endpoint (realtime.streamelements.com), kept as a fallback.</summary>
    LegacyRealtime,
}

public sealed class StreamElementsSettings
{
    /// <summary>JWT token (StreamElements dashboard → Account → Channels → Show secrets). A secret.</summary>
    public string JwtToken { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>Also publish relayed platform activities (follows, subs, cheers, raids...). Off by default.</summary>
    public bool RelayPlatformEvents { get; set; }

    public StreamElementsTransport Transport { get; set; } = StreamElementsTransport.Astro;

    /// <summary>StreamElements channel id of the token, cached from the REST API (not a secret).</summary>
    public string ChannelId { get; set; } = "";

    /// <summary>Channel name of the token, for display.</summary>
    public string ChannelName { get; set; } = "";
}
