namespace Cuebot.Integrations;

/// <summary>Persisted integration configuration (integrations.json). Tokens and keys are never logged.</summary>
public sealed class IntegrationsSettings
{
    public VTubeStudioSettings VTubeStudio { get; set; } = new();
    public VoicemodSettings Voicemod { get; set; } = new();
    public PulsoidSettings Pulsoid { get; set; } = new();
    public HypeRateSettings HypeRate { get; set; } = new();
}

public sealed class VTubeStudioSettings
{
    public string Host { get; set; } = "localhost";

    /// <summary>VTube Studio → Settings → "Start API (allow plugins)" shows the port (default 8001).</summary>
    public int Port { get; set; } = 8001;

    /// <summary>Plugin token issued by VTube Studio after the user approved Cuebot. A secret; renewed automatically.</summary>
    public string AuthToken { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Shown in VTube Studio's plugin list (3-32 characters).</summary>
    public string PluginName { get; set; } = "Cuebot";

    public string PluginDeveloper { get; set; } = "Cuebot contributors";
}

public sealed class VoicemodSettings
{
    public string Host { get; set; } = "localhost";

    /// <summary>Port to try first; 0 = the documented default 59129. The other documented ports are tried after it.</summary>
    public int Port { get; set; }

    /// <summary>Also cycle through Voicemod's documented fallback ports.</summary>
    public bool TryAllPorts { get; set; } = true;

    /// <summary>The Control API client key ("API key") Voicemod issued to the user. A secret.</summary>
    public string ClientKey { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>The port that last worked (tried first next time; not a secret).</summary>
    public int LastPort { get; set; }
}

public sealed class PulsoidSettings
{
    /// <summary>Access token from pulsoid.net/ui/keys (scope data:heart_rate:read). A secret.</summary>
    public string AccessToken { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Use the legacy v1 realtime endpoint instead of v2.</summary>
    public bool UseLegacyV1 { get; set; }
}

public sealed class HypeRateSettings
{
    /// <summary>The developer websocket API key from hyperate.io/api. A secret.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The HypeRate ID / session id shown in the HypeRate app (e.g. "12AB"); "internal-testing" sends fake data.</summary>
    public string SessionId { get; set; } = "";

    public bool AutoConnect { get; set; }

    /// <summary>Also join the clips channel for "Twitch clip created" events.</summary>
    public bool SubscribeClips { get; set; } = true;
}
