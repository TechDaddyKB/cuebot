using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's Twitch poll, prediction, raid, shoutout, tag, moderator/VIP, chat mode, shield mode, reward editing,
/// team and Guest Star methods, plus the remaining YouTube chat and bulk user-variable methods, on the platform
/// modules' script functions. Twitch's functions take text arguments, so numbers and flags are passed as text.
/// Like elsewhere, bool methods report a failed request as false with a warning.
/// </summary>
public sealed partial class CphProxy
{
    // ---- Polls ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a poll of 2-5 choices for <paramref name="duration"/> seconds (15-1800). Twitch no longer offers Bits
    /// voting, so <paramref name="bitsPerVote"/> is ignored. True when the poll started.
    /// </summary>
    public bool TwitchPollCreate(string title, List<string> choices, int duration, int bitsPerVote = 0, int channelPointsPerVote = 0) =>
        !string.IsNullOrWhiteSpace(title)
        && Try(() => Call("twitch.createPoll", new
        {
            title,
            choices = string.Join("\n", Ids(choices)),
            duration = Num(Math.Clamp(duration, 15, 1800)),
            pointsPerVote = Num(Math.Max(0, channelPointsPerVote)),
        }));

    // Cuebot ends the channel's active poll; Twitch allows only one, so the id is not needed.

    /// <summary>Ends the active poll, keeping its results visible.</summary>
    public void TwitchPollTerminate(string pollId) => Try(() => Call("twitch.endPoll", new { mode = "Terminate" }));

    /// <summary>Ends the active poll and hides its results.</summary>
    public void TwitchPollArchive(string pollId) => Try(() => Call("twitch.endPoll", new { mode = "Archive" }));

    // ---- Predictions ----------------------------------------------------------------------------------------------

    /// <summary>Starts a prediction of 2-10 outcomes open for <paramref name="duration"/> seconds; returns its id, or null.</summary>
    public string? TwitchPredictionCreate(string title, List<string> options, int duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var created = TryCallForArgs("twitch.createPrediction",
            new { title, outcomes = string.Join("\n", Ids(options)), window = Num(Math.Clamp(duration, 30, 1800)) }, warn: true);
        return created is null ? null : Arg(created, "predictionId");
    }

    public string? TwitchPredictionCreate(string title, string firstOption, string secondOption, int duration) =>
        TwitchPredictionCreate(title, [firstOption, secondOption], duration);

    // Cuebot acts on the channel's open prediction; Twitch allows only one, so the id is not needed.

    public void TwitchPredictionLock(string predictionId) => Try(() => CallForArgs("twitch.lockPrediction", null));

    /// <summary>Cancels the open prediction, refunding the points.</summary>
    public void TwitchPredictionCancel(string predictionId) => Try(() => CallForArgs("twitch.cancelPrediction", null));

    /// <summary>Resolves the open prediction; <paramref name="winningId"/> is the outcome's id (its title or 1-based number also work).</summary>
    public void TwitchPredictionResolve(string predictionId, string winningId) =>
        Try(() => CallForArgs("twitch.resolvePrediction", new { winner = winningId?.Trim() ?? "", title = "" }));

    // ---- Raids and shoutouts --------------------------------------------------------------------------------------

    public bool TwitchStartRaidByName(string userName) => TwitchUserStep("twitch.startRaid", Login(userName));
    public bool TwitchStartRaidById(string userId) => TwitchUserStep("twitch.startRaid", UserId(userId));
    public bool TwitchCancelRaid() => Try(() => Call("twitch.cancelRaid", null));

    public bool TwitchSendShoutoutByLogin(string userLogin) => TwitchUserStep("twitch.sendShoutout", Login(userLogin));
    public bool TwitchSendShoutoutById(string userId) => TwitchUserStep("twitch.sendShoutout", UserId(userId));

    // ---- Channel tags ---------------------------------------------------------------------------------------------

    public bool TwitchAddChannelTag(string tag) => !string.IsNullOrWhiteSpace(tag) && Try(() => Call("twitch.addTag", new { tag = tag.Trim() }));
    public bool TwitchRemoveChannelTag(string tag) => !string.IsNullOrWhiteSpace(tag) && Try(() => Call("twitch.removeTag", new { tag = tag.Trim() }));
    public bool TwitchClearChannelTags() => Try(() => Call("twitch.clearTags", null));

    /// <summary>Replaces the channel's tags (up to 10).</summary>
    public bool TwitchSetChannelTags(List<string> tags) => Try(() => Call("twitch.setTags", new { tags = string.Join(",", Ids(tags)) }));

    // ---- Moderators and VIPs --------------------------------------------------------------------------------------

    public bool TwitchAddModerator(string userName) => TwitchUserStep("twitch.addModerator", Login(userName));
    public bool TwitchRemoveModerator(string userName) => TwitchUserStep("twitch.removeModerator", Login(userName));
    public bool TwitchAddVip(string userName) => TwitchUserStep("twitch.addVip", Login(userName));
    public bool TwitchRemoveVip(string userName) => TwitchUserStep("twitch.removeVip", Login(userName));

    private bool TwitchUserStep(string function, string user, [CallerMemberName] string method = "") =>
        user is not ("" or "id:") && Try(() => Call(function, new { user }, method), method);

    // ---- Chat modes and shield mode -------------------------------------------------------------------------------

    public void TwitchEmoteOnly(bool enabled = true) => Try(() => Call("twitch.emoteOnly", new { enabled = Flag(enabled) }));
    public void TwitchSubscriberOnly(bool enabled = true) => Try(() => Call("twitch.subscriberMode", new { enabled = Flag(enabled) }));

    /// <summary>Follower-only chat; <paramref name="duration"/> is how long (in minutes) users must have followed.</summary>
    public void TwitchFollowMode(bool enabled = true, int duration = 0) =>
        Try(() => Call("twitch.followerMode", new { enabled = Flag(enabled), minutes = Num(Math.Clamp(duration, 0, 129600)) }));

    /// <summary>Slow mode; <paramref name="duration"/> is the wait between messages in seconds (3-120; 0 uses 30).</summary>
    public void TwitchSlowMode(bool enabled = true, int duration = 0) =>
        Try(() => Call("twitch.slowMode", new { enabled = Flag(enabled), seconds = Num(duration <= 0 ? 30 : Math.Clamp(duration, 3, 120)) }));

    public bool TwitchShieldModeOn() => Try(() => Call("twitch.setShieldMode", new { enabled = "true" }));
    public bool TwitchShieldModeOff() => Try(() => Call("twitch.setShieldMode", new { enabled = "false" }));

    private static string Flag(bool on) => on ? "true" : "false";

    // ---- Rewards --------------------------------------------------------------------------------------------------
    // Twitch lets Cuebot change only the rewards its Twitch app created (TwitchReward.IsOurs).

    /// <summary>Changes the given reward fields (null ones stay). True when Twitch accepted every change.</summary>
    public bool UpdateReward(string rewardId, string? title = null, string? prompt = null, int? cost = null, string? backgroundColor = null)
    {
        var ok = true;
        if (title is not null || prompt is not null || cost is not null)
        {
            ok &= Try(() => Call("twitch.updateReward", new
            {
                reward = rewardId ?? "",
                title = title ?? "",
                prompt = prompt ?? "",
                cost = cost is { } c ? Num(Math.Max(1, c)) : "",
                @operator = "Set",
            }));
        }
        if (!string.IsNullOrWhiteSpace(backgroundColor)) ok &= UpdateRewardBackgroundColor(rewardId, backgroundColor);
        return ok;
    }

    public bool UpdateRewardTitle(string rewardId, string title) => !string.IsNullOrWhiteSpace(title) && UpdateReward(rewardId, title: title);

    public bool UpdateRewardPrompt(string rewardId, string prompt) => UpdateReward(rewardId, prompt: prompt ?? "");

    /// <summary>Sets the reward's colour ("#9147FF").</summary>
    public bool UpdateRewardBackgroundColor(string rewardId, string backgroundColor) =>
        !string.IsNullOrWhiteSpace(backgroundColor)
        && Try(() => Call("twitch.setRewardColor", new { reward = rewardId ?? "", color = backgroundColor.Trim() }));

    /// <summary>Sets (or with <paramref name="additive"/>, changes by) the reward's cost; at least 1.</summary>
    public void UpdateRewardCost(string rewardId, int cost, bool additive = false) =>
        Try(() => Call("twitch.updateReward", new
        {
            reward = rewardId ?? "",
            title = "",
            prompt = "",
            cost = Num(additive ? cost : Math.Max(1, cost)),
            @operator = additive ? "Add" : "Set",
        }));

    /// <summary>Sets (or with <paramref name="additive"/>, raises) the reward's global cooldown in seconds; 0 turns it off.</summary>
    public void UpdateRewardCooldown(string rewardId, int cooldown, bool additive = false)
    {
        var seconds = cooldown;
        if (additive)
        {
            var info = TryCallForArgs("twitch.getRewardInfo", new { reward = rewardId ?? "" }, warn: true);
            if (info is null) return;
            seconds += IntArg(info, "rewardCooldown");
        }
        Try(() => Call("twitch.setRewardCooldown", new { reward = rewardId ?? "", seconds = Num(Math.Max(0, seconds)) }));
    }

    // Reward groups are set on Cuebot's Twitch page.
    public void TwitchRewardGroupEnable(string groupName) => RewardGroup("twitch.setRewardGroupEnabled", groupName, "Enabled");
    public void TwitchRewardGroupDisable(string groupName) => RewardGroup("twitch.setRewardGroupEnabled", groupName, "Disabled");
    public void TwitchRewardGroupToggleEnable(string groupName) => RewardGroup("twitch.setRewardGroupEnabled", groupName, "Toggle");
    public void TwitchRewardGroupPause(string groupName) => RewardGroup("twitch.setRewardGroupPaused", groupName, "Paused");
    public void TwitchRewardGroupUnPause(string groupName) => RewardGroup("twitch.setRewardGroupPaused", groupName, "Unpaused");
    public void TwitchRewardGroupTogglePause(string groupName) => RewardGroup("twitch.setRewardGroupPaused", groupName, "Toggle");

    private void RewardGroup(string function, string groupName, string state, [CallerMemberName] string method = "") =>
        Try(() => CallForArgs(function, new { group = groupName ?? "", state }, method), method);

    // ---- Subscriptions and teams ----------------------------------------------------------------------------------

    /// <summary>Whether the user (by id) subscribes to the channel; <paramref name="tier"/> is Twitch's tier ("1000", "2000", "3000").</summary>
    public bool TwitchIsUserSubscribed(string userId, out string tier)
    {
        tier = "";
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var sub = TryCallForArgs("twitch.getSubStatus", new { user = UserId(userId) }, warn: true);
        if (sub is null || !Flag(sub, "isSubscribed")) return false;
        tier = Arg(sub, "subTier");
        return true;
    }

    /// <summary>The Twitch teams of a channel (by id). Cuebot fills the team names only.</summary>
    public List<TeamInfo> GetTeamInfoById(string userId) => Teams(UserId(userId));

    public List<TeamInfo> GetTeamInfoByLogin(string userLogin) => Teams(Login(userLogin));

    private List<TeamInfo> Teams(string user, [CallerMemberName] string method = "")
    {
        if (user is "" or "id:") return [];
        var teams = TryCallForArgs("twitch.getTeamInfo", new { user }, method, warn: true);
        if (teams is null || IntArg(teams, "teamCount") == 0) return [];
        return Arg(teams, "teams").Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new TeamInfo { Name = name, TeamName = name, TeamDisplayName = name })
            .ToList();
    }

    // ---- Guest Star (Cuebot acts on the channel's active session) -------------------------------------------------

    /// <summary>The channel's Guest Star settings, or null when Twitch refused.</summary>
    public GuestStarSettings? TwitchGetChannelGuestStarSettings()
    {
        var s = TryCallForArgs("twitch.getGuestStarSettings", null, warn: true);
        return s is null ? null : new GuestStarSettings
        {
            SlotCount = IntArg(s, "guestStarSlotCount"),
            GroupLayout = Arg(s, "guestStarLayout"),
            IsModeratorSendLiveEnabled = Flag(s, "guestStarModeratorSendLive"),
            IsBrowserSourceAudioEnabled = Flag(s, "guestStarBrowserSourceAudio"),
            BrowserSourceToken = Arg(s, "guestStarBrowserSourceToken"),
        };
    }

    /// <summary>Changes the given settings (null ones stay).</summary>
    public bool TwitchUpdateChannelGuestStarSettings(bool? isModeratorSendLiveEnabled = null, int? slotCount = null, bool? isBrowserSourceAudioEnabled = null,
        string? groupLayout = null, bool? regeneratgeBrowserSource = null) =>
        Try(() => Call("twitch.updateGuestStarSettings", new
        {
            slotCount = Num(slotCount ?? 0),
            layout = string.IsNullOrWhiteSpace(groupLayout) ? "Keep" : groupLayout.Trim(),
            moderatorSendLive = Keep(isModeratorSendLiveEnabled),
            browserSourceAudio = Keep(isBrowserSourceAudioEnabled),
            regenerateBrowserSources = Flag(regeneratgeBrowserSource == true),
        }));

    /// <summary>The active session with its guests, or null when there is none.</summary>
    public GuestSession? TwitchGetGuestStarSession() => GuestSession("twitch.getGuestStarSession");

    /// <summary>Starts a session (or returns the active one); null when Twitch refused.</summary>
    public GuestSession? TwitchCreateGuestStarSession() => GuestSession("twitch.createGuestStarSession");

    /// <summary>Ends the active session; returns it as it was, or null when there was none.</summary>
    public GuestSession? TwitchEndGuestStarSession()
    {
        var session = TwitchGetGuestStarSession();
        if (session is null) return null;
        return Try(() => Call("twitch.endGuestStarSession", new { sessionId = session.Id })) ? session : null;
    }

    public List<GuestStarInvite> TwitchGetGuestStarInvites()
    {
        var invites = TryCallForArgs("twitch.getGuestStarInvites", new { sessionId = "" }, warn: true);
        if (invites is null) return [];
        return Enumerable.Range(0, IntArg(invites, "guestStarInviteCount")).Select(i => new GuestStarInvite
        {
            UserId = Arg(invites, $"guestStarInvite{i}UserId"),
            Status = Arg(invites, $"guestStarInvite{i}Status"),
        }).ToList();
    }

    public bool TwitchSendGuestStarInvite(string userLogin) => GuestStep("twitch.sendGuestStarInvite", new { user = Login(userLogin), sessionId = "" });
    public bool TwitchDeleteGuestStarInvite(string userLogin) => GuestStep("twitch.deleteGuestStarInvite", new { user = Login(userLogin), sessionId = "" });

    /// <summary>Puts an invited guest into a slot.</summary>
    public bool TwitchAssignGuestStarSlot(string userLogin, int slot) =>
        GuestStep("twitch.assignGuestStarSlot", new { user = Login(userLogin), slot = Num(slot), sessionId = "" });

    /// <summary>Moves the guest in <paramref name="sourceSlot"/> to <paramref name="destinationSlot"/>.</summary>
    public bool TwitchUpdateGuestStarSlot(int sourceSlot, int destinationSlot) =>
        GuestStep("twitch.updateGuestStarSlot", new { slot = Num(sourceSlot), toSlot = Num(destinationSlot), sessionId = "" });

    /// <summary>Removes a guest from their slot.</summary>
    public bool TwitchDeleteGuestStarSlot(string userLogin, int slot) =>
        GuestStep("twitch.deleteGuestStarSlot", new { user = Login(userLogin), slot = Num(slot), reinvite = "false", sessionId = "" });

    /// <summary>Changes a slot's audio, video, live state or volume (0-100); null ones stay.</summary>
    public bool TwitchUpdateGuestStarSlotSettings(int slotId, bool? isAudioEnabled = null, bool? isVideoEnabled = null, bool? isLive = null, int? volume = null) =>
        GuestStep("twitch.updateGuestStarSlotSettings", new
        {
            slot = Num(slotId),
            audio = Keep(isAudioEnabled),
            video = Keep(isVideoEnabled),
            live = Keep(isLive),
            volume = Num(volume is { } v ? Math.Clamp(v, 0, 100) : -1),
            sessionId = "",
        });

    private bool GuestStep(string function, object args, [CallerMemberName] string method = "") => Try(() => Call(function, args, method), method);

    private GuestSession? GuestSession(string function, [CallerMemberName] string method = "")
    {
        var s = TryCallForArgs(function, null, method, warn: true);
        if (s is null || Arg(s, "guestStarSessionId").Length == 0) return null;
        return new GuestSession
        {
            Id = Arg(s, "guestStarSessionId"),
            Guests = Enumerable.Range(0, IntArg(s, "guestStarGuestCount")).Select(i => new GuestStar
            {
                SlotId = Arg(s, $"guestStarGuest{i}Slot"),
                IsLive = Flag(s, $"guestStarGuest{i}IsLive"),
                UserId = Arg(s, $"guestStarGuest{i}UserId"),
                UserLogin = Arg(s, $"guestStarGuest{i}UserName"),
                UserName = Arg(s, $"guestStarGuest{i}"),
                Volume = IntArg(s, $"guestStarGuest{i}Volume"),
            }).ToList(),
        };
    }

    private static string Keep(bool? value) => value switch
    {
        true => "On",
        false => "Off",
        null => "Keep",
    };

    // ---- YouTube chat ---------------------------------------------------------------------------------------------

    /// <summary>Sends to a broadcast's chat (by id); the latest monitored broadcast when <paramref name="broadcastId"/> is null.</summary>
    public void SendYouTubeMessage(string message, bool useBot, string? broadcastId) => SendYouTubeMessage(message, useBot, true, broadcastId);

    /// <summary>Sends to the chat of every broadcast Cuebot monitors.</summary>
    public void SendYouTubeMessageToAllMonitored(string message, bool bot = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var all = Call("youtube.getMonitoredBroadcasts", null);
        if (all.ValueKind != JsonValueKind.Array) return;
        foreach (var id in all.EnumerateArray().Select(b => Prop(b, "id")).Where(i => i.Length > 0))
            Try(() => Call("youtube.sendMessage", new { message, useBot = bot, fallback = true, broadcastId = id }));
    }

    // ---- Bulk and whole-user variables ----------------------------------------------------------------------------

    /// <summary>Sets the variable for every listed Twitch user id.</summary>
    public void SetTwitchUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true)
    {
        foreach (var id in Ids(userIds)) SetUserVarById("twitch", id, varName, value, persisted);
    }

    public void SetYouTubeUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true)
    {
        foreach (var id in Ids(userIds)) SetUserVarById("youtube", id, varName, value, persisted);
    }

    /// <summary>Adds <paramref name="value"/> to the variable of each listed Kick user that has it; others are skipped.</summary>
    public void IncrementKickUsersVarById(List<string> userIds, string varName, object? value, bool persisted = true) =>
        IncrementByIds("kick", userIds, varName, value, persisted, create: false);

    /// <summary>Deletes every variable of a user (by name). Cuebot keeps one set per user, so <paramref name="persisted"/> is ignored.</summary>
    public void UnsetTwitchUser(string userName, bool persisted = true) => UnsetUserByName("twitch", userName);
    public void UnsetTwitchUserById(string userId, bool persisted = true) => UnsetUserById("twitch", userId);
    public void UnsetYouTubeUser(string userName, bool persisted = true) => UnsetUserByName("youtube", userName);
    public void UnsetYouTubeUserById(string userId, bool persisted = true) => UnsetUserById("youtube", userId);
    public void UnsetKickUser(string userName, bool persisted = true) => UnsetUserByName("kick", userName);
    public void UnsetKickUserById(string userId, bool persisted = true) => UnsetUserById("kick", userId);

    private void UnsetUserByName(string platform, string userName)
    {
        if (!string.IsNullOrWhiteSpace(userName) && FindUserId(platform, userName) is { } id) UserVars().DeleteUser(platform, id);
    }

    private void UnsetUserById(string platform, string userId)
    {
        if (!string.IsNullOrWhiteSpace(userId)) UserVars().DeleteUser(platform, userId.Trim());
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
