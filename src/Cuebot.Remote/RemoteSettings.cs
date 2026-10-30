using Cuebot.Core.Storage;
namespace Cuebot.Remote;

/// <summary>
/// remote.json: this instance's identity and the other Cuebot instances it connects to. Every property is optional so
/// older files keep loading. Tokens are stored here like the API token in servers.json and are never logged or shown.
/// </summary>
public sealed class RemoteSettings
{
    public int Version { get; set; } = 1;

    /// <summary>Stable id announced to other instances (generated on first start).</summary>
    public Guid InstanceId { get; set; }

    /// <summary>Name announced to other instances; empty = the computer name.</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>A received remote signal also releases this instance's "Wait for local signal" runs of the same name.</summary>
    public bool ReleaseLocalWaiters { get; set; } = true;

    public List<RemoteInstanceDefinition> Instances { get; set; } = [];
}

/// <summary>Another Cuebot instance this one connects to (its REST API must be on; LAN access for other computers).</summary>
public sealed class RemoteInstanceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Local label used to address the instance in sub-actions ("Send remote signal" → Instance).</summary>
    public string Name { get; set; } = "New instance";

    /// <summary>Host name or IP of the other computer, or a full ws:// / wss:// URL (e.g. behind a reverse proxy).</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>The other instance's REST API port.</summary>
    public int Port { get; set; } = Cuebot.Server.Api.ApiServerSettings.DefaultPort;

    /// <summary>The other instance's REST API token (Servers page there). Never logged.</summary>
    [Secret]
    public string Token { get; set; } = "";

    /// <summary>Connect when Cuebot starts and keep reconnecting.</summary>
    public bool Enabled { get; set; } = true;

    public int ReconnectSeconds { get; set; } = 5;
}
