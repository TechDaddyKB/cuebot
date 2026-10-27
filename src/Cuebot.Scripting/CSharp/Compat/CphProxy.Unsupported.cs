namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot members Cuebot deliberately does not provide. They exist so scripts that use them still compile;
/// using one throws <see cref="NotSupportedException"/> naming it.
/// </summary>
public sealed partial class CphProxy
{
    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchClientId => throw Unsupported("Cuebot does not expose platform credentials to scripts");

    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchOAuthToken => throw Unsupported("Cuebot does not expose platform credentials to scripts");
}
