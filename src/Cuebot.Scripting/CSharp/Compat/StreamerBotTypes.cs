// Types with Streamer.bot's public namespaces and names, so `using` lines at the top of existing Streamer.bot C#
// scripts resolve and their data classes keep working. Only the members Cuebot can fill are provided.
#pragma warning disable IDE0130 // namespaces intentionally mirror Streamer.bot's
#pragma warning disable CA1050

namespace Streamer.bot.Plugin.Interface
{
    /// <summary>
    /// Base of Streamer.bot-style <c>CPHInline</c> classes: <c>CPH</c> and <c>args</c> are this run's (they are
    /// per-run, so concurrent runs of one script do not see each other's arguments).
    /// </summary>
    public abstract class CPHInlineBase
    {
        private static readonly AsyncLocal<Cuebot.Scripting.Compat.CphProxy?> CurrentProxy = new();
        private static readonly AsyncLocal<Dictionary<string, object?>?> CurrentArgs = new();

        /// <summary>Streamer.bot-compatible host methods.</summary>
        public Cuebot.Scripting.Compat.CphProxy CPH =>
            CurrentProxy.Value ?? throw new InvalidOperationException("CPH is only available while the script runs");

        /// <summary>The action run's arguments (case-insensitive; changes are kept for later sub-actions).</summary>
        public Dictionary<string, object?> args
        {
            get => CurrentArgs.Value ?? throw new InvalidOperationException("args is only available while the script runs");
            set => CurrentArgs.Value = value;
        }

        internal static void Enter(Cuebot.Scripting.Compat.CphProxy proxy, Dictionary<string, object?> runArgs)
        {
            CurrentProxy.Value = proxy;
            CurrentArgs.Value = runArgs;
        }
    }
}

namespace Streamer.bot.Plugin.Interface.Enums
{
    /// <summary>Chat platforms of Streamer.bot's API.</summary>
    public enum Platform
    {
        Twitch = 0,
        YouTube = 1,
        Kick = 2,
    }
}

namespace Streamer.bot.Common.Events
{
    /// <summary>Event sources (the <c>source</c> of a WebSocket event).</summary>
    public enum EventSource
    {
        None = 0,
        General,
        Application,
        Command,
        Custom,
        FileTail,
        FileWatcher,
        Group,
        Kick,
        Misc,
        Obs,
        Raw,
        SpeakerBot,
        Twitch,
        WebsocketClient,
        WebsocketCustomServer,
        YouTube,
        Inputs,
        Midi,
        StreamElements,
        Streamlabs,
        Kofi,
        VTubeStudio,
        VoiceMod,
    }

    /// <summary>Event types (source + type). <see cref="None"/> when the run was not started by a known event.</summary>
    public enum EventType
    {
        None = 0,
        TwitchChatMessage,
        TwitchFollow,
        TwitchSub,
        TwitchReSub,
        TwitchGiftSub,
        TwitchGiftBomb,
        TwitchCheer,
        TwitchRaid,
        TwitchRewardRedemption,
        TwitchStreamOnline,
        TwitchStreamOffline,
        TwitchFirstWord,
        TwitchPresentViewers,
        TwitchWhisper,
        TwitchAnnouncement,
        TwitchChatMessageDeleted,
        TwitchUserBanned,
        TwitchUserTimedOut,
        YouTubeMessage,
        YouTubeSuperChat,
        YouTubeSuperSticker,
        YouTubeNewSponsor,
        YouTubeMemberMileStone,
        YouTubeMembershipGift,
        YouTubeNewSubscriber,
        YouTubeFirstWords,
        YouTubePresentViewers,
        KickChatMessage,
        KickFollow,
        KickSubscription,
        KickResubscription,
        KickGiftSubscription,
        KickMassGiftSubscription,
        KickFirstWords,
        KickPresentViewers,
        ObsConnected,
        ObsDisconnected,
        ObsSceneChanged,
        ObsStreamingStarted,
        ObsStreamingStopped,
        ObsRecordingStarted,
        ObsRecordingStopped,
        CommandTriggered,
        CommandCooldown,
        GroupAdded,
        GroupRemoved,
        GroupCleared,
        GroupUsersAdded,
        GroupUsersRemoved,
        MiscGlobalVariableCreated,
        MiscGlobalVariableUpdated,
        MiscGlobalVariableDeleted,
        MiscUserGlobalVariableUpdated,
        MiscStreamerbotStarted,
        MiscTimedAction,
        MiscProcessStarted,
        MiscProcessStopped,
        FileWatcherCreated,
        FileWatcherChanged,
        FileWatcherDeleted,
        FileWatcherRenamed,
        FileTailChanged,
        CustomCodeEvent,
        WebsocketClientOpen,
        WebsocketClientClose,
        WebsocketClientMessage,
        WebsocketCustomServerOpen,
        WebsocketCustomServerClose,
        WebsocketCustomServerMessage,
    }
}

namespace Streamer.bot.Plugin.Interface.Model
{
    /// <summary>An action as returned by <c>CPH.GetActions()</c>.</summary>
    public sealed class ActionData
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public bool Enabled { get; set; }
        public int SubActionCount { get; set; }
        public string Queue { get; set; } = "";
    }

    /// <summary>A command as returned by <c>CPH.GetCommands()</c>.</summary>
    public sealed class CommandData
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public bool Enabled { get; set; }
        public List<string> Commands { get; set; } = [];
        public bool CaseSensitive { get; set; }
        public double GlobalCooldown { get; set; }
        public double UserCooldown { get; set; }
    }

    /// <summary>A global variable as returned by <c>CPH.GetGlobalVarValues()</c>.</summary>
    public sealed class GlobalVariableValue
    {
        public string VariableName { get; set; } = "";
        public object? Value { get; set; }
        public DateTime LastWrite { get; set; }
    }

    /// <summary>One user's value of a user variable (<c>CPH.GetTwitchUsersVar&lt;T&gt;</c> and similar).</summary>
    public sealed class UserVariableValue<T>
    {
        public string UserId { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserName { get; set; } = "";
        public string VariableName { get; set; } = "";
        public T? Value { get; set; }
        public DateTime LastWrite { get; set; }
    }

    /// <summary>A member of a group (<c>CPH.UsersInGroup</c>).</summary>
    public sealed class GroupUser
    {
        public string Id { get; set; } = "";
        public string Login { get; set; } = "";
        public string Username { get; set; } = "";
        public string Type { get; set; } = "";
    }
}
