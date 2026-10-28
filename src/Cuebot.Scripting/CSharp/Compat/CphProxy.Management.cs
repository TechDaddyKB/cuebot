using System.Text.Json;
using Cuebot.Core.Commands;
using Cuebot.Core.Users;
using Streamer.bot.Plugin.Interface.Enums;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Commands (by id or name) ---------------------------------------------------------------------------------

    public void EnableCommand(string id) => Commands().SetEnabled(Command(id).Id, true);
    public void DisableCommand(string id) => Commands().SetEnabled(Command(id).Id, false);

    public List<CommandData> GetCommands() => Commands().All().Select(c => new CommandData
    {
        Id = c.Id,
        Name = c.Name,
        Group = c.Group,
        Enabled = c.Enabled,
        Commands = [.. c.Commands],
        CaseSensitive = c.CaseSensitive,
        GlobalCooldown = c.GlobalCooldownSeconds,
        UserCooldown = c.UserCooldownSeconds,
    }).ToList();

    public long CommandGetCounter(string id) => Commands().GetCounter(Command(id).Id);
    public void CommandResetCounter(string id) => Commands().ResetCounter(Command(id).Id);
    public void CommandResetAllUserCounters(string id) => Commands().ResetUserCounters(Command(id).Id);

    // Cooldowns. Like Streamer.bot, "Reset" starts a cooldown again with its full duration, "Remove" ends it now and
    // "AddTo" adds seconds (starting it when it is not running).

    public void CommandResetGlobalCooldown(string id)
    {
        var command = Command(id);
        Cooldowns().Restart(command.Id.ToString(), null, TimeSpan.FromSeconds(command.GlobalCooldownSeconds));
    }

    public void CommandResetAllUserCooldowns(string id)
    {
        var command = Command(id);
        Cooldowns().RestartUsers(command.Id.ToString(), TimeSpan.FromSeconds(command.UserCooldownSeconds));
    }

    public void CommandResetUserCooldown(string id, string userId, Platform platform)
    {
        var command = Command(id);
        Cooldowns().Restart(command.Id.ToString(), CooldownUser(platform, userId), TimeSpan.FromSeconds(command.UserCooldownSeconds));
    }

    /// <summary>Older form for Twitch users.</summary>
    public void CommandResetUserCooldown(string id, string userId) => CommandResetUserCooldown(id, userId, Platform.Twitch);
    public void CommandResetUserCooldown(string id, int userId) => CommandResetUserCooldown(id, Num(userId), Platform.Twitch);

    public void CommandRemoveGlobalCooldown(string id) => Cooldowns().Clear(Command(id).Id.ToString(), null);
    public void CommandRemoveAllUserCooldowns(string id) => Cooldowns().ClearUsers(Command(id).Id.ToString());

    public void CommandRemoveUserCooldown(string id, string userId, Platform platform) =>
        Cooldowns().Clear(Command(id).Id.ToString(), CooldownUser(platform, userId));

    public void CommandRemoveUserCooldown(string id, string userId) => CommandRemoveUserCooldown(id, userId, Platform.Twitch);
    public void CommandRemoveUserCooldown(string id, int userId) => CommandRemoveUserCooldown(id, Num(userId), Platform.Twitch);

    public void CommandAddToGlobalCooldown(string id, int seconds) =>
        Cooldowns().Extend(Command(id).Id.ToString(), null, TimeSpan.FromSeconds(seconds));

    public void CommandAddToAllUserCooldowns(string id, int seconds) =>
        Cooldowns().ExtendUsers(Command(id).Id.ToString(), TimeSpan.FromSeconds(seconds));

    public void CommandAddToUserCooldown(string id, string userId, Platform platform, int seconds) =>
        Cooldowns().Extend(Command(id).Id.ToString(), CooldownUser(platform, userId), TimeSpan.FromSeconds(seconds));

    public void CommandAddToUserCooldown(string id, string userId, int seconds) => CommandAddToUserCooldown(id, userId, Platform.Twitch, seconds);
    public void CommandAddToUserCooldown(string id, int userId, int seconds) => CommandAddToUserCooldown(id, Num(userId), Platform.Twitch, seconds);

    // User counters.

    /// <summary>How often the user (by login) ran the command; null when the command does not exist.</summary>
    public CommandCounter? CommandGetUserCounter(string userLogin, Platform platform, string commandId) =>
        UserCounter(platform, FindUserId(Key(platform), userLogin) ?? "", Login(userLogin), commandId);

    /// <summary>How often the user (by id) ran the command; null when the command does not exist.</summary>
    public CommandCounter? CommandGetUserCounterById(string userId, Platform platform, string commandId) =>
        UserCounter(platform, userId?.Trim() ?? "", UserName(platform, userId?.Trim() ?? ""), commandId);

    public void CommandResetUserCounter(string commandId, string userId, Platform platform) =>
        Commands().ResetUserCounters(Command(commandId).Id, CommandStore.UserKey(PlatformName(platform), userId?.Trim() ?? "", ""));

    /// <summary>
    /// Resets every user counter of the command for one platform. Cuebot keeps command counters persisted, so
    /// <paramref name="persisted"/> is ignored.
    /// </summary>
    public void CommandResetUsersCounters(string commandId, Platform platform, bool persisted)
    {
        var prefix = Key(platform) + ":";
        Commands().ResetUserCountersWhere(Command(commandId).Id, key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private CommandCounter? UserCounter(Platform platform, string userId, string userName, string commandId)
    {
        var command = Commands().Find(commandId);
        if (command is null)
        {
            LogWarn($"No command '{commandId}'");
            return null;
        }
        var count = Commands().GetUserCounter(command.Id, CommandStore.UserKey(PlatformName(platform), userId, userName));
        return new CommandCounter
        {
            UserId = userId,
            UserLogin = userName.ToLowerInvariant(),
            UserName = userName,
            Platform = PlatformName(platform),
            Count = (int)Math.Min(int.MaxValue, count),
        };
    }

    /// <summary>The user key Cuebot's command cooldowns use ("platform:userId").</summary>
    private static string CooldownUser(Platform platform, string userId) =>
        CommandStore.UserKey(PlatformName(platform), userId?.Trim() ?? "", "");

    private static string Num(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private CooldownTracker Cooldowns([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.CommandService, method).Cooldowns;

    public void CommandSetGlobalCooldownDuration(string id, int seconds) => EditCommand(id, c => c.GlobalCooldownSeconds = Math.Max(0, seconds));
    public void CommandSetUserCooldownDuration(string id, int seconds) => EditCommand(id, c => c.UserCooldownSeconds = Math.Max(0, seconds));

    private void EditCommand(string id, Action<CommandDefinition> edit)
    {
        var command = Command(id);
        edit(command);
        Commands().Save(command);
    }

    private CommandDefinition Command(string idOrName) =>
        Commands().Find(idOrName) ?? throw new ArgumentException($"No command '{idOrName}'", nameof(idOrName));

    private CommandStore Commands([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.Commands, method);

    // ---- Groups ---------------------------------------------------------------------------------------------------

    public bool AddGroup(string groupName) => Groups().Add(groupName);
    public bool DeleteGroup(string groupName) => Groups().Remove(groupName);
    public List<string> GetGroups() => [.. Groups().Names];
    public bool GroupExists(string groupName) => Groups().Exists(groupName);
    public bool ClearUsersFromGroup(string groupName) => Groups().Clear(groupName) >= 0;

    public List<GroupUser> UsersInGroup(string groupName) => Groups().UsersIn(groupName).Select(m => new GroupUser
    {
        Id = m.UserId,
        Login = m.UserName.ToLowerInvariant(),
        Username = m.UserName,
        Type = m.Platform,
    }).ToList();

    public bool AddUserToGroup(string userName, Platform platform, string groupName) =>
        Groups().AddUser(groupName, PlatformName(platform), FindUserId(Key(platform), userName) ?? "", userName.Trim().TrimStart('@'));

    public bool AddUserIdToGroup(string userId, Platform platform, string groupName) =>
        Groups().AddUser(groupName, PlatformName(platform), userId, UserName(platform, userId));

    public bool RemoveUserFromGroup(string userName, Platform platform, string groupName) =>
        Groups().RemoveUser(groupName, PlatformName(platform), FindUserId(Key(platform), userName) ?? "", userName.Trim().TrimStart('@'));

    public bool RemoveUserIdFromGroup(string userId, Platform platform, string groupName) =>
        Groups().RemoveUser(groupName, PlatformName(platform), userId, "");

    public bool UserInGroup(string userName, Platform platform, string groupName) =>
        Groups().Contains(groupName, PlatformName(platform), FindUserId(Key(platform), userName) ?? "", userName.Trim().TrimStart('@'));

    public bool UserIdInGroup(string userId, Platform platform, string groupName) =>
        Groups().Contains(groupName, PlatformName(platform), userId, "");

    // Older platform-less names (Twitch users).
    public bool AddUserToGroup(string userName, string groupName) => AddUserToGroup(userName, Platform.Twitch, groupName);
    public bool RemoveUserFromGroup(string userName, string groupName) => RemoveUserFromGroup(userName, Platform.Twitch, groupName);
    public bool UserInGroup(string userName, string groupName) => UserInGroup(userName, Platform.Twitch, groupName);

    private static string PlatformName(Platform platform) => platform.ToString();
    private static string Key(Platform platform) => platform.ToString().ToLowerInvariant();

    private string UserName(Platform platform, string userId) =>
        _services.Users?.Get(PlatformName(platform), userId)?.UserName ?? "";

    private GroupStore Groups([System.Runtime.CompilerServices.CallerMemberName] string method = "") =>
        Need(_services.Groups, method);

    // ---- Timers (by name or id) -----------------------------------------------------------------------------------

    public void EnableTimer(string timerName) => SetTimer(timerName, true);
    public void DisableTimer(string timerName) => SetTimer(timerName, false);
    public void EnableTimerById(string timerId) => SetTimer(timerId, true);
    public void DisableTimerById(string timerId) => SetTimer(timerId, false);

    public bool GetTimerState(string timerId) =>
        (Need(_services.Timers).GetState(timerId) ?? throw new ArgumentException($"No timer '{timerId}'", nameof(timerId))).Enabled;

    public void SetTimerInterval(string timerId, int interval)
    {
        if (!Need(_services.Timers).SetState(timerId, null, interval)) throw new ArgumentException($"No timer '{timerId}'", nameof(timerId));
    }

    private void SetTimer(string idOrName, bool enabled, [System.Runtime.CompilerServices.CallerMemberName] string method = "")
    {
        if (!Need(_services.Timers, method).SetState(idOrName, enabled)) throw new ArgumentException($"No timer '{idOrName}'", nameof(idOrName));
    }

    // ---- Custom (code) triggers -----------------------------------------------------------------------------------

    /// <summary>Registers a "Custom code event" trigger choice (usually from Init()). Returns false for an empty event name.</summary>
    public bool RegisterCustomTrigger(string triggerName, string eventName, string[]? categories = null) =>
        _services.CodeTriggers.Register(triggerName, eventName, categories);

    /// <summary>Fires the custom trigger <paramref name="eventName"/>, passing this run's arguments when <paramref name="useArgs"/>.</summary>
    public void TriggerCodeEvent(string eventName, bool useArgs = true) =>
        Need(_services.Bus).Publish(CodeTriggerRegistry.CreateEvent(eventName, useArgs ? _context?.Args : null));

    public void TriggerCodeEvent(string eventName, Dictionary<string, object?> args) =>
        Need(_services.Bus).Publish(CodeTriggerRegistry.CreateEvent(eventName, args));

    // ---- WebSocket server / custom servers / clients (provided by the Servers module) -----------------------------

    /// <summary>Sends JSON to every client of the Streamer.bot-compatible WebSocket server (event General.Custom).</summary>
    public void WebsocketBroadcastJson(string data) => Call("websocket.broadcast", new { data, json = true });

    public void WebsocketBroadcastString(string data) => Call("websocket.broadcast", new { data, json = false });

    public void WebsocketConnect(int connection = 0) => Call("websocket.clientConnect", new { connection });
    public void WebsocketDisconnect(int connection = 0) => Call("websocket.clientDisconnect", new { connection });
    public bool WebsocketIsConnected(int connection = 0) => Call("websocket.clientIsConnected", new { connection }).ValueKind == JsonValueKind.True;
    public void WebsocketSend(string data, int connection = 0) => Call("websocket.clientSend", new { connection, data });
    public int WebsocketGetConnectionByName(string name) => Call("websocket.clientIndex", new { name }).GetInt32();

    public void WebsocketCustomServerStart(int connection = 0) => Call("websocket.serverStart", new { connection });
    public void WebsocketCustomServerStop(int connection = 0) => Call("websocket.serverStop", new { connection });
    public bool WebsocketCustomServerIsListening(int connection = 0) =>
        Call("websocket.serverIsListening", new { connection }).ValueKind == JsonValueKind.True;

    /// <summary>Sends to every session of a custom server, or only <paramref name="sessionId"/> when given.</summary>
    public void WebsocketCustomServerBroadcast(string data, string? sessionId = null, int connection = 0) =>
        Call("websocket.serverBroadcast", new { connection, data, sessionId = sessionId ?? "" });

    public void WebsocketCustomServerCloseSession(string sessionId, int connection = 0) =>
        Call("websocket.serverCloseSession", new { connection, sessionId });

    public void WebsocketCustomServerCloseAllSessions(int connection = 0) => Call("websocket.serverCloseAll", new { connection });

    public int WebsocketCustomServerGetConnectionByName(string name) => Call("websocket.serverIndex", new { name }).GetInt32();
}
