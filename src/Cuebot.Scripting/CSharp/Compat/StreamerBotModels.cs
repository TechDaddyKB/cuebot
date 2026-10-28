// More of Streamer.bot's documented data classes (CS:4.classes). Members follow the shapes published in Streamer.bot's
// changelogs and wiki; where those are silent, the members are the obvious ones of the Twitch, Kick or YouTube data
// behind the class. Cuebot fills what it knows and leaves the rest at their defaults.
#pragma warning disable IDE0130 // namespaces intentionally mirror Streamer.bot's
#pragma warning disable CA1050

namespace Streamer.bot.Plugin.Interface.Model
{
    /// <summary>A Twitch channel point reward (<c>CPH.TwitchGetRewards</c>).</summary>
    public class TwitchReward
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public int Cost { get; set; }
        public bool InputRequired { get; set; }
        public string BackgroundColor { get; set; } = "";
        public bool Paused { get; set; }
        public bool Enabled { get; set; }

        /// <summary>True when Cuebot's Twitch app created the reward, so it can change it.</summary>
        public bool IsOurs { get; set; }

        /// <summary>Cuebot extension: the reward group set on the Twitch page.</summary>
        public string Group { get; set; } = "";
    }

    /// <summary>A reward's redemption counter, overall or for one user (<c>CPH.TwitchGetRewardCounter</c> and similar).</summary>
    public class TwitchRewardCounter
    {
        public string UserId { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserName { get; set; } = "";
        public int Count { get; set; }

        /// <summary>Not tracked by Cuebot (always default).</summary>
        public DateTime LastRedeemed { get; set; }
    }

    /// <summary>How often a user ran a command (<c>CPH.CommandGetUserCounter</c>).</summary>
    public class CommandCounter
    {
        public string UserId { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Platform { get; set; } = "";
        public int Count { get; set; }
        public int Source { get; set; }

        /// <summary>Not tracked by Cuebot (always default).</summary>
        public DateTime LastUsed { get; set; }
    }

    /// <summary>A quote (<c>CPH.GetQuote</c>).</summary>
    public class QuoteData
    {
        public DateTime Timestamp { get; set; }
        public int Id { get; set; }
        public string UserId { get; set; } = "";
        public string User { get; set; } = "";
        public string Platform { get; set; } = "";
        public string GameId { get; set; } = "";
        public string GameName { get; set; } = "";
        public string Quote { get; set; } = "";
    }

    /// <summary>A Twitch team a channel belongs to (<c>CPH.GetTeamInfoById</c>, <c>CPH.GetTeamInfoByLogin</c>).</summary>
    public class TeamInfo
    {
        public string Id { get; set; } = "";
        public string Login { get; set; } = "";
        public string Name { get; set; } = "";
        public string BackgroundImageUrl { get; set; } = "";
        public string Banner { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string Info { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public string TeamName { get; set; } = "";
        public string TeamDisplayName { get; set; } = "";
        public string TeamId { get; set; } = "";
    }

    /// <summary>A Kick category (<c>CPH.KickSetCategory</c>).</summary>
    public class KickCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Thumbnail { get; set; } = "";
    }

    /// <summary>A YouTube account (<c>CPH.YouTubeGetBroadcaster</c>, <c>CPH.YouTubeGetBot</c>).</summary>
    public class YouTubeUserInfo
    {
        /// <summary>The channel title.</summary>
        public string UserName { get; set; } = "";

        /// <summary>The channel handle (custom URL) when it has one, else the channel title.</summary>
        public string UserLogin { get; set; } = "";

        /// <summary>The channel id.</summary>
        public string UserId { get; set; } = "";
        public DateTime LastActive { get; set; }
        public DateTime PreviousActive { get; set; }

        /// <summary>Cuebot extension: the channel's profile picture.</summary>
        public string ProfileImageUrl { get; set; } = "";
    }

    /// <summary>The model position in VTube Studio (<c>CPH.VTubeStudioGetModelPosition</c>).</summary>
    public class VTSModelPosition
    {
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        public double Rotation { get; set; }
        public double Size { get; set; }
    }

    /// <summary>Guest Star settings of the channel.</summary>
    public class GuestStarSettings
    {
        public bool IsModeratorSendLiveEnabled { get; set; }
        public int SlotCount { get; set; }
        public bool IsBrowserSourceAudioEnabled { get; set; }
        public string GroupLayout { get; set; } = "";
        public string BrowserSourceToken { get; set; } = "";
    }

    /// <summary>A Guest Star session and its guests.</summary>
    public class GuestSession
    {
        public string Id { get; set; } = "";
        public List<GuestStar> Guests { get; set; } = [];
    }

    /// <summary>A guest in a Guest Star session.</summary>
    public class GuestStar
    {
        public string SlotId { get; set; } = "";
        public bool IsLive { get; set; }
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public int Volume { get; set; }
        public DateTime AssignedAt { get; set; }
        public MediaSettings AudioSettings { get; set; } = new();
        public MediaSettings VideoSettings { get; set; } = new();
    }

    /// <summary>A Guest Star guest's audio or video settings.</summary>
    public class MediaSettings
    {
        public bool IsHostEnabled { get; set; }
        public bool IsGuestEnabled { get; set; }
        public bool IsAvailable { get; set; }
    }

    /// <summary>A pending Guest Star invite.</summary>
    public class GuestStarInvite
    {
        public string UserId { get; set; } = "";
        public DateTime InvitedAt { get; set; }
        public string Status { get; set; } = "";
        public bool IsVideoEnabled { get; set; }
        public bool IsAudioEnabled { get; set; }
        public bool IsVideoAvailable { get; set; }
        public bool IsAudioAvailable { get; set; }
    }

    /// <summary>A Twitch cheermote (<c>CPH.GetCheermotes</c>).</summary>
    public class Cheermote
    {
        /// <summary>The word viewers type, such as "Cheer".</summary>
        public string Prefix { get; set; } = "";
        public List<CheermoteTier> Tiers { get; set; } = [];

        /// <summary>global_first_party, global_third_party, channel_custom, display_only or sponsored.</summary>
        public string Type { get; set; } = "";
        public int Order { get; set; }
        public DateTime LastUpdated { get; set; }
        public bool IsCharitable { get; set; }
    }

    /// <summary>One bits tier of a cheermote.</summary>
    public class CheermoteTier
    {
        public int MinBits { get; set; }
        public string Id { get; set; } = "";

        /// <summary>Hex colour, such as "#9c3ee8".</summary>
        public string Color { get; set; } = "";
        public CheermoteImages Images { get; set; } = new();
        public bool CanCheer { get; set; }
        public bool ShowInBitsCard { get; set; }
    }

    /// <summary>A cheermote tier's images for the dark and light themes.</summary>
    public class CheermoteImages
    {
        public CheermoteImage Dark { get; set; } = new();
        public CheermoteImage Light { get; set; } = new();
    }

    /// <summary>Animated and static image URLs of one theme, keyed by scale ("1", "1.5", "2", "3", "4").</summary>
    public class CheermoteImage
    {
        public Dictionary<string, string> Animated { get; set; } = [];
        public Dictionary<string, string> Static { get; set; } = [];
    }

    /// <summary>Cuebot: the pinned Twitch chat message (<c>CPH.TwitchGetPinnedMessage</c>).</summary>
    public class TwitchPinnedMessage
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserName { get; set; } = "";
        public string PinnedBy { get; set; } = "";
        public DateTime StartsAt { get; set; }

        /// <summary>When the pin ends; default when it stays until the stream ends.</summary>
        public DateTime EndsAt { get; set; }
    }
}
