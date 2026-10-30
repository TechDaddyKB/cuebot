namespace Cuebot.Donations;

/// <summary>Persisted donation service configuration (donations.json). Tokens and keys are never logged.</summary>
public sealed class DonationsSettings
{
    public StreamlabsSettings Streamlabs { get; set; } = new();
    public StreamElementsSettings StreamElements { get; set; } = new();
    public TipeeeStreamSettings TipeeeStream { get; set; } = new();
    public DonorDriveSettings DonorDrive { get; set; } = new();
    public TreatStreamSettings TreatStream { get; set; } = new();
    public PallySettings Pally { get; set; } = new();
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

public sealed class TipeeeStreamSettings
{
    /// <summary>API key (tipeeestream.com dashboard → My account / API). A secret.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>TipeeeStream user name, sent when joining the room (the server does not appear to check it).</summary>
    public string Username { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>Also publish the follows, subs, cheers, raids and hosts TipeeeStream relays. Off by default.</summary>
    public bool RelayPlatformEvents { get; set; }
}

/// <summary>DonorDrive charity programs with a known API host.</summary>
public enum DonorDriveProgram
{
    /// <summary>Extra Life (Children's Miracle Network Hospitals): https://dd.extra-life.org.</summary>
    ExtraLife,

    /// <summary>DonorDrive's own test site (https://testdrive.donordrive.com).</summary>
    TestDrive,

    /// <summary>Any other DonorDrive-powered program: <see cref="DonorDriveSettings.CustomBaseUrl"/>.</summary>
    Custom,
}

public enum DonorDriveProfileType
{
    Participant,
    Team,
}

public sealed class DonorDriveSettings
{
    public DonorDriveProgram Program { get; set; } = DonorDriveProgram.ExtraLife;

    /// <summary>Site of a DonorDrive program not in the list, e.g. https://www.example-charity.org (the /api path is added).</summary>
    public string CustomBaseUrl { get; set; } = "";

    public DonorDriveProfileType ProfileType { get; set; } = DonorDriveProfileType.Participant;

    /// <summary>Participant or team id (the number in the page URL, e.g. participantID=123456). Public, not a secret.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Seconds between polls. DonorDrive asks for at most one request every 15 seconds.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Currency of the program's amounts (DonorDrive does not send one).</summary>
    public string Currency { get; set; } = "USD";

    public bool AutoConnect { get; set; } = true;
}

public sealed class TreatStreamSettings
{
    /// <summary>OAuth client id of the user's TreatStream API app.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth client secret of that app. A secret.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Port of the sign-in redirect, http://localhost:{port}/callback (register it in the TreatStream app).</summary>
    public int RedirectPort { get; set; } = DefaultRedirectPort;

    /// <summary>The port registered for Cuebot's built-in TreatStream app.</summary>
    public const int DefaultRedirectPort = 8913;

    /// <summary>OAuth access token (from signing in, or pasted). A secret.</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>OAuth refresh token. A secret.</summary>
    public string RefreshToken { get; set; } = "";

    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    public bool AutoConnect { get; set; } = true;
}

public sealed class PallySettings
{
    /// <summary>API key (pally.gg → Dashboard → Settings → API Keys). A secret.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Only this page's tips (its slug, pally.gg/p/&lt;slug&gt;); empty = every page you own (firehose).</summary>
    public string PageSlug { get; set; } = "";

    public bool AutoConnect { get; set; } = true;
}
