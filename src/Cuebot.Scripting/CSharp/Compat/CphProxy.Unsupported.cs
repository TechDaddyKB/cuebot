namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot methods Cuebot does not provide (yet). They exist so scripts that use them still compile; calling one
/// throws <see cref="NotSupportedException"/> naming it. Methods returning data are typed <c>dynamic</c> so member
/// access on the result compiles too.
/// </summary>
public sealed partial class CphProxy
{
    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchClientId => throw Unsupported("Cuebot does not expose platform credentials to scripts");

    /// <summary>Not exposed: Cuebot never hands its platform tokens to scripts.</summary>
    public string TwitchOAuthToken => throw Unsupported("Cuebot does not expose platform credentials to scripts");

    public void SendWhisper(string userName, string message, bool bot = true) => throw Unsupported();
    public void TwitchAnnounce(string message, bool bot = false, string? color = null, bool fallback = false) => throw Unsupported();
    public dynamic TwitchGetBroadcaster() => throw Unsupported();
    public dynamic TwitchGetBot() => throw Unsupported();
    public dynamic TwitchGetUserInfoByLogin(string userLogin) => throw Unsupported();
    public dynamic TwitchGetUserInfoById(string userId) => throw Unsupported();
    public dynamic TwitchGetExtendedUserInfoByLogin(string userLogin) => throw Unsupported();
    public dynamic TwitchGetExtendedUserInfoById(string userId) => throw Unsupported();
    public bool TwitchTimeoutUser(string userName, int duration, string? reason = null, bool useBot = false) => throw Unsupported();
    public bool TwitchBanUser(string userName, string? reason = null, bool useBot = false) => throw Unsupported();
    public bool TwitchUnbanUser(string userName, bool useBot = false) => throw Unsupported();
    public void TwitchRunCommercial(int length) => throw Unsupported();
    public dynamic CreateStreamMarker(string description) => throw Unsupported();
    public dynamic CreateClip() => throw Unsupported();
    public bool SetChannelTitle(string title) => throw Unsupported();
    public bool SetChannelGame(string game) => throw Unsupported();
    public dynamic YouTubeGetLatestBroadcast() => throw Unsupported();
    public dynamic KickGetBroadcaster() => throw Unsupported();
    public void KeyboardPress(string keyPress) => throw Unsupported();
    public void TriggerEvent(string eventName, bool useArgs = true) => throw Unsupported("use TriggerCodeEvent for custom triggers");
    public bool ExecuteMethod(string codeName, string methodName) => throw Unsupported();
    public void SendAction(string action, bool bot = true, bool fallback = true) => throw Unsupported();
}
