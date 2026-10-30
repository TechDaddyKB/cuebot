using Cuebot.Core.Storage;
namespace Cuebot.Relay;

/// <summary>relay.json: where the user's Cloudflare relay (relay/ in the repository) lives and how to sign in to it.</summary>
public sealed class RelaySettings
{
    /// <summary>The Worker's address as printed by the setup script, e.g. <c>https://cuebot-relay.name.workers.dev</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>Bearer token for <c>/connect</c> (the Worker's CONNECT_TOKEN secret). Secret: masked in the UI, never logged.</summary>
    [Secret]
    public string ConnectToken { get; set; } = "";

    /// <summary>
    /// The Worker's ROUTE_SECRET: the unguessable path segment of every webhook URL. Cuebot only needs it to show
    /// those URLs. Secret: anyone who knows it can post fake (unsigned) webhooks, which the handlers then reject.
    /// </summary>
    [Secret]
    public string RouteSecret { get; set; } = "";

    /// <summary>Use the relay at all. Off until the user has deployed one.</summary>
    public bool Enabled { get; set; }

    /// <summary>Connect when Cuebot starts (when <see cref="Enabled"/> and the settings are complete).</summary>
    public bool AutoConnect { get; set; } = true;
}
