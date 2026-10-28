using Streamer.bot.Plugin.Interface.Model;

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

    /// <summary>Not supported: Cuebot counts redemptions per reward, not the points users spent. Returns 0.</summary>
    public long TwitchGetChannelPointsUsedByUserId(string userId) =>
        NotSupported(0L, "Cuebot counts redemptions per reward but does not track the channel points users spent");

    private const string NoBlockedUsersScope = "Cuebot does not request Twitch's user:manage:blocked_users scope";

    /// <summary>Not supported (needs a Twitch scope Cuebot does not request). Returns false.</summary>
    public bool TwitchUnblockUser(string userLogin) => NotSupported(false, NoBlockedUsersScope);

    /// <summary>Not supported (needs a Twitch scope Cuebot does not request). Returns false.</summary>
    public bool TwitchUnblockUserById(string userId) => NotSupported(false, NoBlockedUsersScope);

    /// <summary>Not supported: Cuebot does not keep a per-user tally of cheered Bits. Returns 0.</summary>
    public long TwitchGetBitsDonatedByUserId(string userId) => NotSupported(0L, "Cuebot does not keep a per-user tally of cheered Bits");

    /// <summary>Not supported: Cuebot sets the Twitch category by name (use SetChannelGame). Returns false.</summary>
    public bool SetChannelGameById(string gameId) => NotSupported(false, "Cuebot sets the Twitch category by name; use SetChannelGame");

    // ---- Trovo and VStream: Cuebot connects to neither ------------------------------------------------------------

    private const string NoTrovo = "Cuebot has no Trovo connection";
    private const string NoVStream = "Cuebot has no VStream connection";

    /// <summary>Not supported (no Trovo connection). Returns -1.</summary>
    public int AddQuoteForTrovo(string userId, string quote, bool captureGame = false) => NotSupported(-1, NoTrovo);

    public void SendTrovoMessage(string message, bool useBot = true, bool fallback = true) => NotSupported(0, NoTrovo);
    public TrovoUserInfo? TrovoGetBroadcaster() => NotSupported<TrovoUserInfo?>(null, NoTrovo);
    public TrovoUserInfo? TrovoGetBot() => NotSupported<TrovoUserInfo?>(null, NoTrovo);
    public bool TrovoSetTitle(string title) => NotSupported(false, NoTrovo);
    public bool TrovoBanUser(string userName, bool bot = false) => NotSupported(false, NoTrovo);
    public bool TrovoUnbanUser(string userName, bool bot = false) => NotSupported(false, NoTrovo);
    public bool TrovoTimeoutUser(string userName, int duration, bool bot = false) => NotSupported(false, NoTrovo);

    public T? GetTrovoUserVar<T>(string userName, string varName, bool persisted = true) => NotSupported<T?>(default, NoTrovo);
    public T? GetTrovoUserVarById<T>(string userId, string varName, bool persisted = true) => NotSupported<T?>(default, NoTrovo);
    public List<UserVariableValue<T>> GetTrovoUsersVar<T>(string varName, bool persisted = true) => NotSupported<List<UserVariableValue<T>>>([], NoTrovo);
    public void SetTrovoUserVar(string userName, string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void SetTrovoUserVarById(string userId, string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void SetTrovoUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void IncrementAllTrovoUsersVar(string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void IncrementTrovoUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void IncrementOrCreateTrovoUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) => NotSupported(0, NoTrovo);
    public void UnsetTrovoUserVar(string userName, string varName, bool persisted = true) => NotSupported(0, NoTrovo);
    public void UnsetTrovoUserVarById(string userId, string varName, bool persisted = true) => NotSupported(0, NoTrovo);
    public void UnsetTrovoUser(string userName, bool persisted = true) => NotSupported(0, NoTrovo);
    public void UnsetTrovoUserById(string userId, bool persisted = true) => NotSupported(0, NoTrovo);

    public T? GetVStreamUserVar<T>(string userName, string varName, bool persisted = true) => NotSupported<T?>(default, NoVStream);
    public T? GetVStreamUserVarById<T>(string userId, string varName, bool persisted = true) => NotSupported<T?>(default, NoVStream);
    public List<UserVariableValue<T>> GetVStreamUsersVar<T>(string varName, bool persisted = true) => NotSupported<List<UserVariableValue<T>>>([], NoVStream);
    public void SetVStreamUserVar(string userName, string varName, object? value, bool persisted = true) => NotSupported(0, NoVStream);
    public void SetVStreamUserVarById(string userId, string varName, object? value, bool persisted = true) => NotSupported(0, NoVStream);
    public void SetVStreamUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) => NotSupported(0, NoVStream);
    public void UnsetVStreamUserVar(string userName, string varName, bool persisted = true) => NotSupported(0, NoVStream);
    public void UnsetVStreamUserVarById(string userId, string varName, bool persisted = true) => NotSupported(0, NoVStream);
    public void UnsetVStreamUser(string userName, bool persisted = true) => NotSupported(0, NoVStream);
    public void UnsetVStreamUserById(string userId, bool persisted = true) => NotSupported(0, NoVStream);

    /// <summary>Logs that the calling method is not supported and returns <paramref name="failure"/>.</summary>
    private T NotSupported<T>(T failure, string reason, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        LogWarn($"CPH.{method} is not supported in Cuebot ({reason})");
        return failure;
    }
}
