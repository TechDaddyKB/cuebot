using System.Text.Json;

namespace Cuebot.Twitch;

/// <summary>Which account sends a chat message or whisper.</summary>
public enum TwitchSender
{
    /// <summary>The bot when it is signed in and the settings say so, otherwise the broadcaster.</summary>
    Default,
    Bot,
    Broadcaster,
}

/// <summary>Announcement colours Twitch accepts.</summary>
public static class AnnouncementColors
{
    public static IReadOnlyList<string> All { get; } = ["primary", "blue", "green", "orange", "purple"];
}

public sealed record TwitchUser(
    string Id, string Login, string DisplayName, string Type, string BroadcasterType, string Description,
    string ProfileImageUrl, string OfflineImageUrl, DateTimeOffset? CreatedAt);

public sealed record TwitchChannelInfo(
    string BroadcasterId, string BroadcasterLogin, string BroadcasterName, string Language, string GameId, string GameName,
    string Title, IReadOnlyList<string> Tags, IReadOnlyList<string> ContentLabels, bool IsBrandedContent);

public sealed record TwitchCategory(string Id, string Name, string BoxArtUrl);

public sealed record TwitchStreamInfo(
    string Id, string UserId, string UserLogin, string GameId, string GameName, string Title, int ViewerCount,
    DateTimeOffset? StartedAt, string Language, IReadOnlyList<string> Tags, bool IsMature);

/// <summary>Fields to change on the channel; null leaves a field unchanged.</summary>
public sealed record ChannelUpdate
{
    public string? Title { get; init; }
    public string? GameId { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool? IsBrandedContent { get; init; }

    /// <summary>Content classification label id → enabled (DrugsIntoxication, SexualThemes, ViolentGraphic, Gambling, ProfanityVulgarity).</summary>
    public IReadOnlyDictionary<string, bool>? ContentLabels { get; init; }
}

public sealed record TwitchFollow(string UserId, string UserLogin, string UserName, DateTimeOffset FollowedAt);

public sealed record TwitchSubscription(string UserId, string UserLogin, string UserName, string Tier, bool IsGift, string GifterName);

public sealed record TwitchCount(int Total, string LatestUserId, string LatestUserLogin, string LatestUserName, int Points = 0);

public sealed record TwitchCommercial(int Length, int RetryAfter, string Message);

public sealed record TwitchAdSchedule(
    DateTimeOffset? NextAdAt, DateTimeOffset? LastAdAt, int Duration, int PrerollFreeTime, int SnoozeCount, DateTimeOffset? SnoozeRefreshAt);

public sealed record TwitchClip(string Id, string EditUrl);

public sealed record TwitchMarker(string Id, int PositionSeconds, string Description);

public sealed record TwitchChatSettings(
    bool EmoteMode, bool FollowerMode, int FollowerModeDuration, bool SlowMode, int SlowModeWaitTime,
    bool SubscriberMode, bool UniqueChatMode);

/// <summary>Chat modes to change; null leaves a mode unchanged. Durations: follower minutes, slow seconds.</summary>
public sealed record ChatSettingsUpdate
{
    public bool? EmoteMode { get; init; }
    public bool? FollowerMode { get; init; }
    public int? FollowerModeDuration { get; init; }
    public bool? SlowMode { get; init; }
    public int? SlowModeWaitTime { get; init; }
    public bool? SubscriberMode { get; init; }
    public bool? UniqueChatMode { get; init; }
}

public sealed record TwitchPollChoice(string Id, string Title, int Votes, int ChannelPointsVotes);

public sealed record TwitchPoll(string Id, string Title, string Status, int Duration, IReadOnlyList<TwitchPollChoice> Choices, DateTimeOffset? StartedAt);

public sealed record TwitchPredictionOutcome(string Id, string Title, string Color, int Users, int ChannelPoints);

public sealed record TwitchPrediction(
    string Id, string Title, string Status, string WinningOutcomeId, int PredictionWindow,
    IReadOnlyList<TwitchPredictionOutcome> Outcomes, DateTimeOffset? CreatedAt);

public sealed record TwitchReward(
    string Id, string Title, string Prompt, int Cost, string BackgroundColor, bool IsEnabled, bool IsPaused, bool IsInStock,
    bool IsUserInputRequired, bool SkipQueue, int MaxPerStream, int MaxPerUserPerStream, int GlobalCooldownSeconds,
    bool IsManageable);

/// <summary>Reward fields to set; null leaves a field unchanged (0 for a limit disables it).</summary>
public sealed record RewardUpdate
{
    public string? Title { get; init; }
    public string? Prompt { get; init; }
    public int? Cost { get; init; }
    public string? BackgroundColor { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsPaused { get; init; }
    public bool? IsUserInputRequired { get; init; }
    public bool? SkipQueue { get; init; }
    public int? MaxPerStream { get; init; }
    public int? MaxPerUserPerStream { get; init; }
    public int? GlobalCooldownSeconds { get; init; }
}

public sealed record TwitchShieldMode(bool IsActive, string ModeratorName, DateTimeOffset? LastActivatedAt);

public sealed record TwitchTeam(string Id, string Name, string DisplayName);

public sealed record TwitchCharityCampaign(
    string Id, string CharityName, string CharityDescription, string CurrentAmount, string TargetAmount, string Currency);

public sealed record TwitchChatter(string UserId, string UserLogin, string UserName);

internal static class HelixParse
{
    public static DateTimeOffset? Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(e.Str(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static TwitchUser User(JsonElement u) => new(u.Str("id"), u.Str("login"), u.Str("display_name"), u.Str("type"),
        u.Str("broadcaster_type"), u.Str("description"), u.Str("profile_image_url"), u.Str("offline_image_url"), Time(u, "created_at"));

    public static TwitchChannelInfo Channel(JsonElement c) => new(c.Str("broadcaster_id"), c.Str("broadcaster_login"),
        c.Str("broadcaster_name"), c.Str("broadcaster_language"), c.Str("game_id"), c.Str("game_name"), c.Str("title"),
        c.Strings("tags"), c.Strings("content_classification_labels"), c.Bool("is_branded_content"));

    public static TwitchStreamInfo Stream(JsonElement s) => new(s.Str("id"), s.Str("user_id"), s.Str("user_login"),
        s.Str("game_id"), s.Str("game_name"), s.Str("title"), s.Int("viewer_count"), Time(s, "started_at"), s.Str("language"),
        s.Strings("tags"), s.Bool("is_mature"));

    public static TwitchPoll Poll(JsonElement p) => new(p.Str("id"), p.Str("title"), p.Str("status"), p.Int("duration"),
        p.Items("choices").Select(c => new TwitchPollChoice(c.Str("id"), c.Str("title"), c.Int("votes"), c.Int("channel_points_votes"))).ToArray(),
        Time(p, "started_at"));

    public static TwitchPrediction Prediction(JsonElement p) => new(p.Str("id"), p.Str("title"), p.Str("status"),
        p.Str("winning_outcome_id"), p.Int("prediction_window"),
        p.Items("outcomes").Select(o => new TwitchPredictionOutcome(o.Str("id"), o.Str("title"), o.Str("color"), o.Int("users"),
            o.Int("channel_points"))).ToArray(),
        Time(p, "created_at"));

    public static TwitchReward Reward(JsonElement r, bool manageable)
    {
        var perStream = r.Child("max_per_stream_setting") ?? default;
        var perUser = r.Child("max_per_user_per_stream_setting") ?? default;
        var cooldown = r.Child("global_cooldown_setting") ?? default;
        return new TwitchReward(r.Str("id"), r.Str("title"), r.Str("prompt"), r.Int("cost"), r.Str("background_color"),
            r.Bool("is_enabled"), r.Bool("is_paused"), r.Bool("is_in_stock"), r.Bool("is_user_input_required"),
            r.Bool("should_redemptions_skip_request_queue"),
            perStream.Bool("is_enabled") ? perStream.Int("max_per_stream") : 0,
            perUser.Bool("is_enabled") ? perUser.Int("max_per_user_per_stream") : 0,
            cooldown.Bool("is_enabled") ? cooldown.Int("global_cooldown_seconds") : 0,
            manageable);
    }

    public static TwitchChatSettings ChatSettings(JsonElement s) => new(s.Bool("emote_mode"), s.Bool("follower_mode"),
        s.Int("follower_mode_duration"), s.Bool("slow_mode"), s.Int("slow_mode_wait_time"), s.Bool("subscriber_mode"),
        s.Bool("unique_chat_mode"));
}
