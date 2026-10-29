namespace Cuebot.Core.Users;

/// <summary>
/// Adds a platform's stream details to its "users.presentViewers" event: what Streamer.bot documents for that platform's
/// Present Viewers trigger (isLive, title, viewerCount, category...) and the user list does not know.
/// </summary>
public interface IPresentViewersArgs
{
    /// <summary>The platform whose events this adds to ("kick", ...).</summary>
    string Platform { get; }

    void Add(IDictionary<string, object?> args);
}
