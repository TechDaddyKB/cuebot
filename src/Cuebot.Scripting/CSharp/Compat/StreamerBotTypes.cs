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

    /// <summary>
    /// A Twitch user (<c>CPH.TwitchGetBroadcaster</c>, <c>TwitchGetBot</c>, <c>TwitchGetUserInfoByLogin/ById</c>).
    /// Activity and roles come from Cuebot's user list (what it has seen in chat); PreviousActive is not tracked.
    /// </summary>
    public class TwitchUserInfo
    {
        /// <summary>Display name.</summary>
        public string UserName { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserId { get; set; } = "";
        public DateTime LastActive { get; set; }
        public DateTime PreviousActive { get; set; }
        public bool IsSubscribed { get; set; }
        public string SubscriptionTier { get; set; } = "";
        public bool IsModerator { get; set; }
        public bool IsVip { get; set; }
    }

    /// <summary>A Twitch user with profile and channel details (<c>CPH.TwitchGetExtendedUserInfoByLogin/ById</c>).</summary>
    public class TwitchUserInfoEx
    {
        public string UserName { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Description { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";

        /// <summary>Broadcaster type: "partner", "affiliate" or "".</summary>
        public string UserType { get; set; } = "";
        public bool IsPartner => string.Equals(UserType, "partner", StringComparison.OrdinalIgnoreCase);
        public bool IsAffiliate => string.Equals(UserType, "affiliate", StringComparison.OrdinalIgnoreCase);
        public bool IsFollowing { get; set; }
        public DateTime LastActive { get; set; }
        public DateTime PreviousActive { get; set; }
        public bool IsSubscribed { get; set; }
        public string SubscriptionTier { get; set; } = "";
        public bool IsModerator { get; set; }
        public bool IsVip { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Account age in seconds.</summary>
        public double AccountAge { get; set; }
        public string Game { get; set; } = "";
        public string GameId { get; set; } = "";
        public string ChannelTitle { get; set; } = "";
        public List<string> Tags { get; set; } = [];
    }

    /// <summary>A Twitch category (<c>CPH.SetChannelGame</c>).</summary>
    public class GameInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string BoxArtUrl { get; set; } = "";
    }

    /// <summary>A YouTube broadcast Cuebot monitors (<c>CPH.YouTubeGetLatestMonitoredBroadcast</c>).</summary>
    public class YouTubeBroadcastInfo
    {
        public DateTime MonitoredAt { get; set; }
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string LiveChatId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string CategoryId { get; set; } = "";
        public string CategoryName { get; set; } = "";
        public List<string> Tags { get; set; } = [];
        public string DefaultLanguage { get; set; } = "";
        public string DefaultAudioLanguage { get; set; } = "";
        public string Privacy { get; set; } = "";
        public DateTime PublishedAt { get; set; }
        public string Status { get; set; } = "";

        /// <summary>Cuebot extension: the watch URL.</summary>
        public string Url { get; set; } = "";
    }

    /// <summary>A Kick account (<c>CPH.KickGetBroadcaster</c>, <c>CPH.KickGetBot</c>).</summary>
    public class KickUserInfo
    {
        public string UserName { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserId { get; set; } = "";

        /// <summary>Cuebot extension: the channel slug (broadcaster only).</summary>
        public string ChannelSlug { get; set; } = "";
    }
}

namespace Twitch.Common.Models.Api
{
    /// <summary>A clip (<c>CPH.CreateClip</c>). Cuebot fills what Twitch returns when the clip is created.</summary>
    public class ClipData
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string EmbedUrl { get; set; } = "";
        public int BroadcasterId { get; set; }
        public string BroadcasterName { get; set; } = "";
        public int CreatorId { get; set; }
        public string CreatorName { get; set; } = "";
        public string VideoId { get; set; } = "";
        public string GameId { get; set; } = "";
        public string Language { get; set; } = "";
        public string Title { get; set; } = "";
        public int ViewCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public string ThumbnailUrl { get; set; } = "";
        public float Duration { get; set; }
        public bool IsFeatured { get; set; }

        /// <summary>Cuebot extension: the clip editor URL.</summary>
        public string EditUrl { get; set; } = "";
    }

    /// <summary>A stream marker (<c>CPH.CreateStreamMarker</c>).</summary>
    public class StreamMarker
    {
        public string Id { get; set; } = "";
        public DateTime CreatedAt { get; set; }

        /// <summary>Position in the stream, in seconds.</summary>
        public int Position { get; set; }
        public string Description { get; set; } = "";
    }
}
