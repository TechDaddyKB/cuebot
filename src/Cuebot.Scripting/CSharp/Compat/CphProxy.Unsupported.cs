namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot members Cuebot deliberately does not provide. They exist so scripts that use them still compile.
/// The credential properties throw <see cref="NotSupportedException"/>; the methods log "not supported in Cuebot"
/// and return Streamer.bot's failure value. docs/SERVERS.md lists them.
/// </summary>
public sealed partial class CphProxy
{
    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchClientId => throw Unsupported("Cuebot does not expose platform credentials to scripts");

    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchOAuthToken => throw Unsupported("Cuebot does not expose platform credentials to scripts");

    private const string NoSuspiciousUsersScope = "Cuebot does not request Twitch's moderator:manage:suspicious_users scope";

    /// <summary>Not supported (needs a Twitch scope Cuebot does not request). Returns false.</summary>
    public bool TwitchAddActiveMonitoringSuspiciousStatus(string userId) => NotSupported(false, NoSuspiciousUsersScope);

    /// <summary>Not supported (needs a Twitch scope Cuebot does not request). Returns false.</summary>
    public bool TwitchAddRestrictedSuspiciousStatus(string userId) => NotSupported(false, NoSuspiciousUsersScope);

    /// <summary>Not supported (needs a Twitch scope Cuebot does not request). Returns false.</summary>
    public bool TwitchRemoveSuspiciousStatus(string userId) => NotSupported(false, NoSuspiciousUsersScope);

    /// <summary>Not supported: Cuebot counts redemptions per reward, not the points users spent. Returns 0.</summary>
    public long TwitchGetChannelPointsUsedByUserId(string userId) =>
        NotSupported(0L, "Cuebot counts redemptions per reward but does not track the channel points users spent");

    /// <summary>Logs that the calling method is not supported and returns <paramref name="failure"/>.</summary>
    private T NotSupported<T>(T failure, string reason, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        LogWarn($"CPH.{method} is not supported in Cuebot ({reason})");
        return failure;
    }
}
