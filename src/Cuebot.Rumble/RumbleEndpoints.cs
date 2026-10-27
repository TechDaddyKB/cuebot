namespace Cuebot.Rumble;

/// <summary>
/// Base addresses of Rumble's web services (unofficial, see PROTOCOL.md); overridable so tests can point at fakes.
/// The Live Stream API URL is not here: it is per user and comes from the settings.
/// </summary>
internal sealed record RumbleEndpoints(Uri Site, Uri Chat)
{
    public static RumbleEndpoints Production { get; } =
        new(new Uri("https://rumble.com/"), new Uri("https://web7.rumble.com/chat/api/chat/"));

    /// <summary>Server-sent-events stream of one live stream's chat.</summary>
    public Uri ChatStream(long chatId) => new(Chat, $"{chatId}/stream");

    /// <summary>Endpoint that posts a message to one live stream's chat.</summary>
    public Uri ChatMessage(long chatId) => new(Chat, $"{chatId}/message");

    /// <summary>Rumble's form-encoded RPC endpoint (login steps), e.g. <c>service.php?name=user.login</c>.</summary>
    public Uri Service(string name) => new(Site, $"service.php?name={Uri.EscapeDataString(name)}");
}
