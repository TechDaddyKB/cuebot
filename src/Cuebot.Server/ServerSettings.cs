using Cuebot.Core.Storage;
namespace Cuebot.Server;

/// <summary>
/// servers.json. Every server is off until enabled in the UI, except MCP, which a saved servers.json turns on (version 2,
/// see ServerManager). Each binds to 127.0.0.1 unless the user turns on <see cref="AllowLan"/> (then a server's
/// address may be 0.0.0.0 or a LAN address). Every property is optional so older files keep loading.
/// </summary>
public sealed class ServerSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = 1;

    /// <summary>Allow binding to non-loopback addresses (LAN). Off: every server listens on 127.0.0.1 only.</summary>
    public bool AllowLan { get; set; }

    /// <summary>
    /// Web origins (e.g. "https://my-overlay.example") allowed to open WebSockets and POST to the HTTP API, besides
    /// local pages (localhost, 127.0.0.1, file:// / OBS local files) and native clients that send no Origin.
    /// "*" allows every origin.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    public StreamerBotWebSocketSettings WebSocket { get; set; } = new();
    public HttpServerSettings Http { get; set; } = new();
    public UdpServerSettings Udp { get; set; } = new();
    public SpeakerBotSettings SpeakerBot { get; set; } = new();
    public List<CustomWebSocketServerDefinition> CustomServers { get; set; } = [];
    public List<CustomWebSocketClientDefinition> CustomClients { get; set; } = [];

    /// <summary>The local REST API (<c>/api/v1</c>, bearer token). Its listener also serves MCP.</summary>
    public Api.ApiServerSettings Api { get; set; } = new();

    /// <summary>The MCP endpoint (<c>/mcp</c>) on the API listener.</summary>
    public Mcp.McpServerSettings Mcp { get; set; } = new();
}

/// <summary>The Streamer.bot-compatible WebSocket server (for @streamerbot/client overlays and tools).</summary>
public sealed class StreamerBotWebSocketSettings
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public string Endpoint { get; set; } = "/";

    /// <summary>Require clients to authenticate with <see cref="Password"/> (Streamer.bot's salt/challenge handshake).</summary>
    public bool AuthenticationEnabled { get; set; }

    /// <summary>Never logged.</summary>
    [Secret]
    public string Password { get; set; } = "";

    /// <summary>With authentication on: true requires it for every request, false only for privileged ones.</summary>
    public bool EnforceAuthentication { get; set; } = true;
}

/// <summary>The HTTP API (DoAction, GetActions, ...), static file hosting and the OBS chat/event docks.</summary>
public sealed class HttpServerSettings
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7474;

    /// <summary>Folders (or single files) served at URL paths, e.g. "/overlays" → "C:\Overlays".</summary>
    public List<HttpMapping> Mappings { get; set; } = [];
}

public sealed class HttpMapping
{
    public string Path { get; set; } = "/";
    public string Folder { get; set; } = "";
}

/// <summary>The UDP DoAction listener.</summary>
public sealed class UdpServerSettings
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4242;
}

/// <summary>Speaker.bot-compatible APIs mapped onto Cuebot's speech.</summary>
public sealed class SpeakerBotSettings
{
    public bool WebSocketEnabled { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public int WebSocketPort { get; set; } = 7580;
    public string Endpoint { get; set; } = "/";
    public bool UdpEnabled { get; set; }
    public int UdpPort { get; set; } = 6669;
}

/// <summary>A user-defined WebSocket server (Streamer.bot "WebSocket Servers").</summary>
public sealed class CustomWebSocketServerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New server";

    /// <summary>Start with Cuebot.</summary>
    public bool AutoStart { get; set; }

    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public string Endpoint { get; set; } = "/";
}

/// <summary>A user-defined outgoing WebSocket connection (Streamer.bot "WebSocket Clients").</summary>
public sealed class CustomWebSocketClientDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New client";
    public string Url { get; set; } = "ws://127.0.0.1:9000/";
    public bool AutoConnect { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectSeconds { get; set; } = 5;
}
