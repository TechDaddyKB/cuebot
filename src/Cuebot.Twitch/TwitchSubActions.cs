using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Twitch.Triggers;

namespace Cuebot.Twitch;

/// <summary>One Twitch sub-action: id, editor parameters and what it does with the API.</summary>
internal sealed record TwitchSubActionSpec(
    string TypeId,
    string DisplayName,
    IReadOnlyList<ParameterDescriptor> Parameters,
    Func<TwitchApi, ActionRunContext, ParameterValues, CancellationToken, Task> Run);

/// <summary>Runs a <see cref="TwitchSubActionSpec"/> against the live <see cref="TwitchApi"/>.</summary>
internal sealed class TwitchSubAction(TwitchSubActionSpec spec, Func<TwitchApi> api) : ISubActionHandler
{
    public string TypeId => spec.TypeId;
    public string Category => TwitchTriggers.Category;
    public string DisplayName => spec.DisplayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => spec.Parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await spec.Run(api(), context, parameters, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>
/// Every Twitch sub-action. Results of "get" actions are written into the action's arguments (listed in each
/// display name's help); a missing scope or sign-in fails the action with a message saying what to enable.
/// </summary>
internal static class TwitchSubActions
{
    private static readonly IReadOnlyList<string> SenderChoices = ["Default", "Bot", "Broadcaster"];

    private static ParameterDescriptor UserParam(string label = "User") =>
        new("user", label) { Help = "Login name (a leading @ is fine) or id:12345. Leave empty for the user who triggered the action." };

    private static ParameterDescriptor RewardParam() =>
        new("reward", "Reward") { DefaultValue = "", Help = "Reward title or id. Leave empty for the redeemed reward (%rewardId%)." };

    private static ParameterDescriptor SendAs() =>
        new("sendAs", "Send as", ParameterKind.Choice)
        {
            DefaultValue = "Default", Choices = SenderChoices,
            Help = "Default uses the bot account when it is signed in (see the Twitch page), otherwise the broadcaster",
        };

    private static ParameterDescriptor Bool(string key, string label, bool value = true) =>
        new(key, label, ParameterKind.Boolean) { DefaultValue = value ? "true" : "false" };

    private static ParameterDescriptor Number(string key, string label, string value, string? help = null) =>
        new(key, label, ParameterKind.Number) { DefaultValue = value, Help = help };

    public static IReadOnlyList<TwitchSubActionSpec> All { get; } =
    [
        // ---------------------------------------------------------------- chat
        new("twitch.sendMessage", "Send chat message", [new("message", "Message", ParameterKind.Multiline), SendAs()],
            (api, _, p, ct) => api.SendChatMessageAsync(p["message"], Sender(p), null, ct)),
        new("twitch.replyToMessage", "Reply to message",
            [new("message", "Message", ParameterKind.Multiline), new("messageId", "Reply to message id") { DefaultValue = "%messageId%" }, SendAs()],
            (api, _, p, ct) => api.SendChatMessageAsync(p["message"], Sender(p), p["messageId"].Trim(), ct)),
        new("twitch.sendWhisper", "Send whisper", [UserParam("To user"), new("message", "Message", ParameterKind.Multiline), SendAs()],
            async (api, c, p, ct) => await api.SendWhisperAsync(UserOrTrigger(c, p["user"]), p["message"], Sender(p), ct).ConfigureAwait(false)),
        new("twitch.sendAnnouncement", "Send announcement",
            [
                new("message", "Message", ParameterKind.Multiline),
                new("color", "Colour", ParameterKind.Choice) { DefaultValue = "primary", Choices = AnnouncementColors.All },
            ],
            (api, _, p, ct) => api.SendAnnouncementAsync(p["message"], p["color"], ct)),
        new("twitch.deleteMessage", "Delete chat message", [new("messageId", "Message id") { DefaultValue = "%messageId%" }],
            (api, _, p, ct) => api.DeleteMessageAsync(p["messageId"], ct)),
        new("twitch.clearChat", "Clear chat", [], (api, _, _, ct) => api.ClearChatAsync(ct)),
        new("twitch.getPresentViewers", "Get present viewers (presentViewerCount, presentViewers)", [],
            async (api, c, _, ct) =>
            {
                var chatters = await api.GetChattersAsync(ct).ConfigureAwait(false);
                c.Args["presentViewerCount"] = chatters.Count;
                c.Args["presentViewers"] = string.Join(",", chatters.Select(x => x.UserLogin));
            }),

        // ---------------------------------------------------------------- channel
        new("twitch.setTitle", "Set channel title", [new("title", "Title")],
            (api, _, p, ct) => api.UpdateChannelAsync(new ChannelUpdate { Title = p["title"] }, ct)),
        new("twitch.setGame", "Set channel category (gameId, gameName)", [new("game", "Category / game name")],
            async (api, c, p, ct) =>
            {
                var category = await api.FindCategoryAsync(p["game"], ct).ConfigureAwait(false)
                               ?? throw new TwitchApiException($"No Twitch category matches '{p["game"]}'.");
                await api.UpdateChannelAsync(new ChannelUpdate { GameId = category.Id }, ct).ConfigureAwait(false);
                c.Args["gameId"] = category.Id;
                c.Args["gameName"] = category.Name;
            }),
        new("twitch.setTags", "Set channel tags", [new("tags", "Tags") { Help = "Comma or space separated, at most 10" }],
            (api, _, p, ct) => api.UpdateChannelAsync(new ChannelUpdate { Tags = SplitTags(p["tags"]) }, ct)),
        new("twitch.addTag", "Add channel tag", [new("tag", "Tag")],
            (api, _, p, ct) => ChangeTagsAsync(api, tags => [.. tags, p["tag"]], ct)),
        new("twitch.removeTag", "Remove channel tag", [new("tag", "Tag")],
            (api, _, p, ct) => ChangeTagsAsync(api, tags => tags.Where(t => !t.Equals(p["tag"].Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase)).ToArray(), ct)),
        new("twitch.clearTags", "Clear channel tags", [],
            (api, _, _, ct) => api.UpdateChannelAsync(new ChannelUpdate { Tags = [] }, ct)),
        new("twitch.setBrandedContent", "Set branded content", [Bool("enabled", "Branded content")],
            (api, _, p, ct) => api.UpdateChannelAsync(new ChannelUpdate { IsBrandedContent = p.GetBool("enabled") }, ct)),
        new("twitch.setContentLabel", "Set content classification label",
            [
                new("label", "Label", ParameterKind.Choice)
                {
                    DefaultValue = "ProfanityVulgarity",
                    Choices = ["DrugsIntoxication", "SexualThemes", "ViolentGraphic", "Gambling", "ProfanityVulgarity"],
                },
                Bool("enabled", "Enabled"),
            ],
            (api, _, p, ct) => api.UpdateChannelAsync(new ChannelUpdate
            {
                ContentLabels = new Dictionary<string, bool> { [p["label"]] = p.GetBool("enabled") },
            }, ct)),
        new("twitch.updateStreamInfo", "Update stream info",
            [
                new("title", "Title") { Help = "Leave empty to keep" },
                new("game", "Category / game") { Help = "Leave empty to keep" },
                new("tags", "Tags") { Help = "Comma separated; leave empty to keep" },
            ],
            async (api, _, p, ct) =>
            {
                string? gameId = null;
                if (p["game"].Trim().Length > 0)
                    gameId = (await api.FindCategoryAsync(p["game"], ct).ConfigureAwait(false)
                              ?? throw new TwitchApiException($"No Twitch category matches '{p["game"]}'.")).Id;
                await api.UpdateChannelAsync(new ChannelUpdate
                {
                    Title = p["title"].Trim().Length > 0 ? p["title"] : null,
                    GameId = gameId,
                    Tags = p["tags"].Trim().Length > 0 ? SplitTags(p["tags"]) : null,
                }, ct).ConfigureAwait(false);
            }),
        new("twitch.getChannelInfo", "Get channel info (channelTitle, channelGame, channelTags...)",
            [new("user", "Channel") { Help = "Login or id:12345; leave empty for your channel" }],
            async (api, c, p, ct) =>
            {
                string? id = p["user"].Trim().Length > 0 ? await api.ResolveUserIdAsync(p["user"], ct).ConfigureAwait(false) : null;
                var channel = await api.GetChannelAsync(id, ct).ConfigureAwait(false) ?? throw new TwitchApiException("Channel not found.");
                c.Args["channelId"] = channel.BroadcasterId;
                c.Args["channelUser"] = channel.BroadcasterName;
                c.Args["channelUserName"] = channel.BroadcasterLogin;
                c.Args["channelTitle"] = channel.Title;
                c.Args["channelGame"] = channel.GameName;
                c.Args["channelGameId"] = channel.GameId;
                c.Args["channelTags"] = string.Join(", ", channel.Tags);
                c.Args["channelLanguage"] = channel.Language;
                c.Args["channelContentLabels"] = string.Join(", ", channel.ContentLabels);
                c.Args["channelIsBrandedContent"] = channel.IsBrandedContent;
            }),
        new("twitch.getStreamInfo", "Get stream info (isLive, viewerCount, streamTitle, uptimeSeconds...)",
            [new("user", "Channel") { Help = "Login or id:12345; leave empty for your channel" }],
            async (api, c, p, ct) =>
            {
                string? id = p["user"].Trim().Length > 0 ? await api.ResolveUserIdAsync(p["user"], ct).ConfigureAwait(false) : null;
                var stream = await api.GetStreamAsync(id, ct).ConfigureAwait(false);
                c.Args["isLive"] = stream is not null;
                c.Args["viewerCount"] = stream?.ViewerCount ?? 0;
                c.Args["streamTitle"] = stream?.Title ?? "";
                c.Args["streamGame"] = stream?.GameName ?? "";
                c.Args["streamStartedAt"] = stream?.StartedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
                c.Args["uptimeSeconds"] = stream?.StartedAt is { } started ? (long)(DateTimeOffset.UtcNow - started).TotalSeconds : 0L;
            }),
        new("twitch.createMarker", "Create stream marker (markerId, markerPosition)", [new("description", "Description")],
            async (api, c, p, ct) =>
            {
                var marker = await api.CreateStreamMarkerAsync(p["description"], ct).ConfigureAwait(false);
                c.Args["markerId"] = marker.Id;
                c.Args["markerPosition"] = marker.PositionSeconds;
            }),
        new("twitch.createClip", "Create clip (clipId, clipUrl, clipEditUrl)", [Bool("hasDelay", "Account for stream delay", false)],
            async (api, c, p, ct) =>
            {
                var clip = await api.CreateClipAsync(p.GetBool("hasDelay"), ct).ConfigureAwait(false);
                c.Args["clipId"] = clip.Id;
                c.Args["clipEditUrl"] = clip.EditUrl;
                c.Args["clipUrl"] = $"https://clips.twitch.tv/{clip.Id}";
            }),

        // ---------------------------------------------------------------- ads
        new("twitch.runAd", "Run commercial",
            [new("length", "Length (seconds)", ParameterKind.Choice) { DefaultValue = "30", Choices = ["30", "60", "90", "120", "150", "180"] }],
            async (api, c, p, ct) =>
            {
                var ad = await api.RunCommercialAsync(p.GetInt("length", 30), ct).ConfigureAwait(false);
                c.Args["adLength"] = ad.Length;
                c.Args["adRetryAfter"] = ad.RetryAfter;
            }),
        new("twitch.snoozeAd", "Snooze next ad (snoozeCount, nextAdAt)", [],
            async (api, c, _, ct) => WriteAdSchedule(c, await api.SnoozeAdAsync(ct).ConfigureAwait(false))),
        new("twitch.getAdSchedule", "Get ad schedule (nextAdAt, prerollFreeTime, snoozeCount...)", [],
            async (api, c, _, ct) => WriteAdSchedule(c, await api.GetAdScheduleAsync(ct).ConfigureAwait(false))),

        // ---------------------------------------------------------------- raids and shoutouts
        new("twitch.startRaid", "Start raid", [UserParam("Channel to raid")],
            (api, c, p, ct) => api.StartRaidAsync(UserOrTrigger(c, p["user"]), ct)),
        new("twitch.cancelRaid", "Cancel raid", [], (api, _, _, ct) => api.CancelRaidAsync(ct)),
        new("twitch.sendShoutout", "Send shoutout", [UserParam("Channel")],
            (api, c, p, ct) => api.SendShoutoutAsync(UserOrTrigger(c, p["user"]), ct)),

        // ---------------------------------------------------------------- moderation
        new("twitch.ban", "Ban user", [UserParam(), new("reason", "Reason")],
            async (api, c, p, ct) => await api.BanAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["reason"], ct).ConfigureAwait(false)),
        new("twitch.timeout", "Timeout user", [UserParam(), Number("duration", "Duration (seconds)", "600", "1 second to 14 days"), new("reason", "Reason")],
            async (api, c, p, ct) => await api.TimeoutAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p.GetInt("duration", 600),
                p["reason"], ct).ConfigureAwait(false)),
        new("twitch.unban", "Unban user", [UserParam()],
            async (api, c, p, ct) => await api.UnbanAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.untimeout", "Remove timeout", [UserParam()],
            async (api, c, p, ct) => await api.UnbanAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.warn", "Warn user", [UserParam(), new("reason", "Reason")],
            async (api, c, p, ct) => await api.WarnAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["reason"], ct).ConfigureAwait(false)),
        new("twitch.addVip", "Add VIP", [UserParam()],
            async (api, c, p, ct) => await api.AddVipAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.removeVip", "Remove VIP", [UserParam()],
            async (api, c, p, ct) => await api.RemoveVipAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.addModerator", "Add moderator", [UserParam()],
            async (api, c, p, ct) => await api.AddModeratorAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.removeModerator", "Remove moderator", [UserParam()],
            async (api, c, p, ct) => await api.RemoveModeratorAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.emoteOnly", "Chat mode: emote-only", [Bool("enabled", "Enabled")],
            (api, _, p, ct) => api.UpdateChatSettingsAsync(new ChatSettingsUpdate { EmoteMode = p.GetBool("enabled") }, ct)),
        new("twitch.followerMode", "Chat mode: follower-only",
            [Bool("enabled", "Enabled"), Number("minutes", "Must follow for (minutes)", "0", "0 to 129600 (3 months)")],
            (api, _, p, ct) => api.UpdateChatSettingsAsync(new ChatSettingsUpdate
            {
                FollowerMode = p.GetBool("enabled"),
                FollowerModeDuration = p.GetBool("enabled") ? p.GetInt("minutes") : null,
            }, ct)),
        new("twitch.slowMode", "Chat mode: slow mode", [Bool("enabled", "Enabled"), Number("seconds", "Delay (seconds)", "30", "3 to 120")],
            (api, _, p, ct) => api.UpdateChatSettingsAsync(new ChatSettingsUpdate
            {
                SlowMode = p.GetBool("enabled"),
                SlowModeWaitTime = p.GetBool("enabled") ? p.GetInt("seconds", 30) : null,
            }, ct)),
        new("twitch.subscriberMode", "Chat mode: subscriber-only", [Bool("enabled", "Enabled")],
            (api, _, p, ct) => api.UpdateChatSettingsAsync(new ChatSettingsUpdate { SubscriberMode = p.GetBool("enabled") }, ct)),
        new("twitch.uniqueChat", "Chat mode: unique chat", [Bool("enabled", "Enabled")],
            (api, _, p, ct) => api.UpdateChatSettingsAsync(new ChatSettingsUpdate { UniqueChatMode = p.GetBool("enabled") }, ct)),
        new("twitch.setShieldMode", "Set shield mode", [Bool("enabled", "Active")],
            (api, _, p, ct) => api.SetShieldModeAsync(p.GetBool("enabled"), ct)),
        new("twitch.getShieldMode", "Get shield mode status (shieldModeActive)", [],
            async (api, c, _, ct) =>
            {
                var shield = await api.GetShieldModeAsync(ct).ConfigureAwait(false);
                c.Args["shieldModeActive"] = shield.IsActive;
                c.Args["shieldModeModerator"] = shield.ModeratorName;
            }),
        new("twitch.addBlockedTerm", "Add blocked term (blockedTermId)", [new("term", "Term")],
            async (api, c, p, ct) => c.Args["blockedTermId"] = await api.AddBlockedTermAsync(p["term"], ct).ConfigureAwait(false)),
        new("twitch.resolveAutomod", "Approve or deny AutoMod message",
            [
                new("messageId", "Held message id") { DefaultValue = "%messageId%" },
                new("decision", "Decision", ParameterKind.Choice) { DefaultValue = "Allow", Choices = ["Allow", "Deny"] },
            ],
            (api, _, p, ct) => api.ResolveAutomodMessageAsync(p["messageId"].Trim(), p["decision"] == "Allow", ct)),

        // ---------------------------------------------------------------- polls
        new("twitch.createPoll", "Create poll (pollId)",
            [
                new("title", "Question"),
                new("choices", "Choices", ParameterKind.Multiline) { Help = "One per line (2 to 5)" },
                Number("duration", "Duration (seconds)", "60", "15 to 1800"),
                Number("pointsPerVote", "Channel points per extra vote", "0", "0 disables channel point voting"),
            ],
            async (api, c, p, ct) => c.Args["pollId"] = (await api.CreatePollAsync(p["title"], Lines(p["choices"]), p.GetInt("duration", 60),
                p.GetInt("pointsPerVote"), ct).ConfigureAwait(false)).Id),
        new("twitch.endPoll", "End active poll",
            [new("mode", "How", ParameterKind.Choice) { DefaultValue = "Terminate", Choices = ["Terminate", "Archive"], Help = "Archive hides the results" }],
            (api, _, p, ct) => api.EndPollAsync(p["mode"] == "Archive", null, ct)),
        new("twitch.getPoll", "Get latest poll (pollId, pollTitle, pollStatus, pollChoice0..)", [],
            async (api, c, _, ct) =>
            {
                var poll = await api.GetLatestPollAsync(ct).ConfigureAwait(false);
                c.Args["pollId"] = poll?.Id ?? "";
                c.Args["pollTitle"] = poll?.Title ?? "";
                c.Args["pollStatus"] = poll?.Status ?? "";
                c.Args["pollIsActive"] = poll?.Status == "ACTIVE";
                var choices = poll?.Choices ?? [];
                c.Args["pollChoiceCount"] = choices.Count;
                for (var i = 0; i < choices.Count; i++)
                {
                    c.Args[$"pollChoice{i}"] = choices[i].Title;
                    c.Args[$"pollChoice{i}Votes"] = choices[i].Votes;
                }
            }),

        // ---------------------------------------------------------------- predictions
        new("twitch.createPrediction", "Create prediction (predictionId)",
            [
                new("title", "Question"),
                new("outcomes", "Outcomes", ParameterKind.Multiline) { Help = "One per line (2 to 10)" },
                Number("window", "Submission window (seconds)", "120", "30 to 1800"),
            ],
            async (api, c, p, ct) => c.Args["predictionId"] = (await api.CreatePredictionAsync(p["title"], Lines(p["outcomes"]),
                p.GetInt("window", 120), ct).ConfigureAwait(false)).Id),
        new("twitch.lockPrediction", "Lock active prediction", [], (api, _, _, ct) => api.LockPredictionAsync(ct)),
        new("twitch.cancelPrediction", "Cancel active prediction (refund)", [], (api, _, _, ct) => api.CancelPredictionAsync(ct)),
        new("twitch.resolvePrediction", "Resolve prediction",
            [
                new("winner", "Winning outcome") { Help = "Outcome title, or its number (1, 2, ...)" },
                new("title", "Only if the prediction is titled") { Help = "Leave empty to resolve the latest prediction" },
            ],
            (api, _, p, ct) => api.ResolvePredictionAsync(p["winner"], p["title"], ct)),
        new("twitch.getPrediction", "Get latest prediction (predictionId, predictionTitle, predictionStatus, predictionOutcome0..)", [],
            async (api, c, _, ct) =>
            {
                var prediction = await api.GetLatestPredictionAsync(ct).ConfigureAwait(false);
                c.Args["predictionId"] = prediction?.Id ?? "";
                c.Args["predictionTitle"] = prediction?.Title ?? "";
                c.Args["predictionStatus"] = prediction?.Status ?? "";
                var outcomes = prediction?.Outcomes ?? [];
                c.Args["predictionOutcomeCount"] = outcomes.Count;
                for (var i = 0; i < outcomes.Count; i++)
                {
                    c.Args[$"predictionOutcome{i}"] = outcomes[i].Title;
                    c.Args[$"predictionOutcome{i}Users"] = outcomes[i].Users;
                    c.Args[$"predictionOutcome{i}Points"] = outcomes[i].ChannelPoints;
                }
            }),

        // ---------------------------------------------------------------- channel point rewards
        new("twitch.createReward", "Create reward (createdRewardId)",
            [
                new("title", "Title"), Number("cost", "Cost", "100"), new("prompt", "Prompt"),
                Bool("inputRequired", "User input required", false), Bool("enabled", "Enabled"),
            ],
            async (api, c, p, ct) => c.Args["createdRewardId"] = (await api.CreateRewardAsync(new RewardUpdate
            {
                Title = p["title"], Cost = p.GetInt("cost", 100), Prompt = p["prompt"],
                IsUserInputRequired = p.GetBool("inputRequired"), IsEnabled = p.GetBool("enabled", true),
            }, ct).ConfigureAwait(false)).Id),
        Reward("twitch.setRewardEnabled", "Reward: set enabled", Bool("enabled", "Enabled"), p => new RewardUpdate { IsEnabled = p.GetBool("enabled") }),
        Reward("twitch.setRewardPaused", "Reward: set paused", Bool("paused", "Paused"), p => new RewardUpdate { IsPaused = p.GetBool("paused") }),
        Reward("twitch.setRewardCost", "Reward: set cost", Number("cost", "Cost", "100"), p => new RewardUpdate { Cost = p.GetInt("cost", 100) }),
        Reward("twitch.setRewardTitle", "Reward: set title", new("title", "Title"), p => new RewardUpdate { Title = p["title"] }),
        Reward("twitch.setRewardPrompt", "Reward: set prompt", new("prompt", "Prompt"), p => new RewardUpdate { Prompt = p["prompt"] }),
        Reward("twitch.setRewardColor", "Reward: set background colour", new("color", "Colour (#RRGGBB)") { DefaultValue = "#9147FF" },
            p => new RewardUpdate { BackgroundColor = p["color"].Trim() }),
        Reward("twitch.setRewardCooldown", "Reward: set global cooldown", Number("seconds", "Cooldown (seconds)", "0", "0 disables"),
            p => new RewardUpdate { GlobalCooldownSeconds = p.GetInt("seconds") }),
        Reward("twitch.setRewardMaxPerStream", "Reward: set max per stream", Number("max", "Maximum", "0", "0 disables"),
            p => new RewardUpdate { MaxPerStream = p.GetInt("max") }),
        Reward("twitch.setRewardMaxPerUser", "Reward: set max per user per stream", Number("max", "Maximum", "0", "0 disables"),
            p => new RewardUpdate { MaxPerUserPerStream = p.GetInt("max") }),
        new("twitch.deleteReward", "Reward: delete", [RewardParam()],
            async (api, c, p, ct) => await api.DeleteRewardAsync((await RewardAsync(api, c, p, ct).ConfigureAwait(false)).Id, ct).ConfigureAwait(false)),
        new("twitch.getRewardInfo", "Reward: get info (rewardTitle, rewardCost, rewardEnabled, rewardPaused...)", [RewardParam()],
            async (api, c, p, ct) =>
            {
                var r = await RewardAsync(api, c, p, ct).ConfigureAwait(false);
                c.Args["rewardId"] = r.Id;
                c.Args["rewardTitle"] = r.Title;
                c.Args["rewardPrompt"] = r.Prompt;
                c.Args["rewardCost"] = r.Cost;
                c.Args["rewardEnabled"] = r.IsEnabled;
                c.Args["rewardPaused"] = r.IsPaused;
                c.Args["rewardInStock"] = r.IsInStock;
                c.Args["rewardBackgroundColor"] = r.BackgroundColor;
                c.Args["rewardCooldown"] = r.GlobalCooldownSeconds;
                c.Args["rewardMaxPerStream"] = r.MaxPerStream;
                c.Args["rewardMaxPerUser"] = r.MaxPerUserPerStream;
                c.Args["rewardIsManageable"] = r.IsManageable;
            }),
        new("twitch.updateRedemption", "Reward: fulfil or cancel redemption",
            [
                new("rewardId", "Reward id") { DefaultValue = "%rewardId%" },
                new("redemptionId", "Redemption id") { DefaultValue = "%redemptionId%" },
                new("status", "Status", ParameterKind.Choice) { DefaultValue = "Fulfil", Choices = ["Fulfil", "Cancel"], Help = "Cancel refunds the points" },
            ],
            (api, _, p, ct) => api.UpdateRedemptionAsync(p["rewardId"], p["redemptionId"], p["status"] != "Cancel", ct)),

        // ---------------------------------------------------------------- users
        new("twitch.getUserInfo", "Get user info (targetUser, targetUserId, targetDescription, targetCreatedAt...)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var target = UserOrTrigger(c, p["user"]);
                var user = await api.GetUserAsync(target, ct).ConfigureAwait(false);
                c.Args["targetFound"] = user is not null;
                if (user is null) return;
                c.Args["targetUser"] = user.DisplayName;
                c.Args["targetUserName"] = user.Login;
                c.Args["targetUserId"] = user.Id;
                c.Args["targetDescription"] = user.Description;
                c.Args["targetProfileImageUrl"] = user.ProfileImageUrl;
                c.Args["targetBroadcasterType"] = user.BroadcasterType;
                c.Args["targetCreatedAt"] = user.CreatedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
                c.Args["targetAccountAgeDays"] = user.CreatedAt is { } created ? (int)(DateTimeOffset.UtcNow - created).TotalDays : 0;
                if (await api.GetChannelAsync(user.Id, ct).ConfigureAwait(false) is { } channel)
                {
                    c.Args["targetGame"] = channel.GameName;
                    c.Args["targetTitle"] = channel.Title;
                }
            }),
        new("twitch.getFollowAge", "Get follow age (isFollowing, followedAt, followAgeDays, followAge)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var follow = await api.GetFollowAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                c.Args["isFollowing"] = follow is not null;
                c.Args["followedAt"] = follow?.FollowedAt.ToString("o", CultureInfo.InvariantCulture) ?? "";
                var age = follow is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - follow.FollowedAt;
                c.Args["followAgeDays"] = (int)age.TotalDays;
                c.Args["followAge"] = follow is null ? "" : HumanizeAge(age);
            }),
        new("twitch.getSubStatus", "Get subscription status (isSubscribed, subTier, subIsGift, subGifter)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var sub = await api.GetSubscriptionAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                c.Args["isSubscribed"] = sub is not null;
                c.Args["subTier"] = sub?.Tier ?? "";
                c.Args["subIsGift"] = sub?.IsGift ?? false;
                c.Args["subGifter"] = sub?.GifterName ?? "";
            }),
        new("twitch.getFollowerCount", "Get follower count (followerCount, latestFollower)", [],
            async (api, c, _, ct) =>
            {
                var count = await api.GetFollowerCountAsync(ct).ConfigureAwait(false);
                c.Args["followerCount"] = count.Total;
                c.Args["latestFollower"] = count.LatestUserName;
                c.Args["latestFollowerUserName"] = count.LatestUserLogin;
                c.Args["latestFollowerUserId"] = count.LatestUserId;
            }),
        new("twitch.getSubscriberCount", "Get subscriber count (subscriberCount, subPoints)", [],
            async (api, c, _, ct) =>
            {
                var count = await api.GetSubscriberCountAsync(ct).ConfigureAwait(false);
                c.Args["subscriberCount"] = count.Total;
                c.Args["subPoints"] = count.Points;
            }),
        new("twitch.getTeamInfo", "Get team info (teams, teamCount)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var teams = await api.GetTeamsAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                c.Args["teamCount"] = teams.Count;
                c.Args["teams"] = string.Join(", ", teams.Select(t => t.DisplayName));
                c.Args["teamName"] = teams.FirstOrDefault()?.DisplayName ?? "";
            }),
        new("twitch.getBroadcasterInfo", "Add broadcaster info (broadcasterUser, broadcasterUserId, broadcasterType, botUser...)", [],
            async (api, c, _, ct) =>
            {
                var (id, login) = api.Broadcaster;
                var user = await api.GetUserByIdAsync(id, ct).ConfigureAwait(false);
                c.Args["broadcasterUserId"] = id;
                c.Args["broadcasterUserName"] = login;
                c.Args["broadcasterUser"] = user?.DisplayName ?? login;
                c.Args["broadcasterType"] = user?.BroadcasterType ?? "";
                c.Args["broadcasterIsAffiliate"] = user?.BroadcasterType == "affiliate";
                c.Args["broadcasterIsPartner"] = user?.BroadcasterType == "partner";
                c.Args["broadcasterProfileImageUrl"] = user?.ProfileImageUrl ?? "";
                c.Args["botUserId"] = api.Bot.UserId;
                c.Args["botUserName"] = api.Bot.Login;
            }),
        new("twitch.getRandomUsers", "Add random present users (randomUser0.., randomUserCount)",
            [Number("count", "How many", "1"), Bool("excludeBroadcaster", "Leave out the broadcaster and the bot")],
            async (api, c, p, ct) =>
            {
                string[] exclude = p.GetBool("excludeBroadcaster", true) ? [api.Broadcaster.UserId, api.Bot.UserId] : [];
                var chatters = (await api.GetChattersAsync(ct).ConfigureAwait(false)).Where(x => !exclude.Contains(x.UserId)).ToArray();
                var picked = chatters.OrderBy(_ => Random.Shared.Next()).Take(Math.Max(1, p.GetInt("count", 1))).ToArray();
                c.Args["randomUserCount"] = picked.Length;
                for (var i = 0; i < picked.Length; i++)
                {
                    c.Args[$"randomUser{i}"] = picked[i].UserName;
                    c.Args[$"randomUserName{i}"] = picked[i].UserLogin;
                    c.Args[$"randomUserId{i}"] = picked[i].UserId;
                }
            }),
        new("twitch.getCharityCampaign", "Get charity campaign (charityName, charityCurrent, charityTarget)", [],
            async (api, c, _, ct) =>
            {
                var charity = await api.GetCharityCampaignAsync(ct).ConfigureAwait(false);
                c.Args["charityActive"] = charity is not null;
                c.Args["charityName"] = charity?.CharityName ?? "";
                c.Args["charityCurrent"] = charity?.CurrentAmount ?? "";
                c.Args["charityTarget"] = charity?.TargetAmount ?? "";
                c.Args["charityCurrency"] = charity?.Currency ?? "";
            }),
    ];

    private static TwitchSubActionSpec Reward(string id, string name, ParameterDescriptor value, Func<ParameterValues, RewardUpdate> update) =>
        new(id, name, [RewardParam(), value], async (api, c, p, ct) =>
        {
            var reward = await RewardAsync(api, c, p, ct).ConfigureAwait(false);
            await api.UpdateRewardAsync(reward.Id, update(p), ct).ConfigureAwait(false);
        });

    private static async Task<TwitchReward> RewardAsync(TwitchApi api, ActionRunContext c, ParameterValues p, CancellationToken ct)
    {
        var key = p["reward"].Trim();
        if (key.Length == 0) key = c.Args.TryGetValue("rewardId", out var id) ? Convert.ToString(id, CultureInfo.InvariantCulture) ?? "" : "";
        if (key.Length == 0) throw new ArgumentException("No reward given.");
        return await api.FindRewardAsync(key, ct).ConfigureAwait(false);
    }

    private static TwitchSender Sender(ParameterValues p) =>
        Enum.TryParse<TwitchSender>(p["sendAs"], ignoreCase: true, out var sender) ? sender : TwitchSender.Default;

    /// <summary>The given user, or "id:..." of the user who triggered the action.</summary>
    internal static string UserOrTrigger(ActionRunContext c, string user)
    {
        if (user.Trim().Length > 0) return user;
        var id = c.Args.TryGetValue("userId", out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        return id.Length > 0 ? "id:" + id : throw new ArgumentException("No Twitch user given, and the trigger has no userId.");
    }

    private static Task<string> UserIdAsync(TwitchApi api, ActionRunContext c, ParameterValues p, CancellationToken ct) =>
        api.ResolveUserIdAsync(UserOrTrigger(c, p["user"]), ct);

    private static async Task ChangeTagsAsync(TwitchApi api, Func<IReadOnlyList<string>, IReadOnlyList<string>> change, CancellationToken ct)
    {
        var channel = await api.GetChannelAsync(null, ct).ConfigureAwait(false) ?? throw new TwitchApiException("Channel not found.");
        await api.UpdateChannelAsync(new ChannelUpdate { Tags = change(channel.Tags) }, ct).ConfigureAwait(false);
    }

    internal static string[] SplitTags(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Lines(string text) =>
        text.Split(['\n', '\r', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void WriteAdSchedule(ActionRunContext c, TwitchAdSchedule s)
    {
        c.Args["nextAdAt"] = s.NextAdAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        c.Args["lastAdAt"] = s.LastAdAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        c.Args["adDuration"] = s.Duration;
        c.Args["prerollFreeTime"] = s.PrerollFreeTime;
        c.Args["snoozeCount"] = s.SnoozeCount;
        c.Args["snoozeRefreshAt"] = s.SnoozeRefreshAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>"2 years, 3 months", "5 days", "3 hours".</summary>
    internal static string HumanizeAge(TimeSpan age)
    {
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        var days = (int)age.TotalDays;
        if (days >= 365)
        {
            var years = days / 365;
            var months = days % 365 / 30;
            return months > 0 ? $"{Unit(years, "year")}, {Unit(months, "month")}" : Unit(years, "year");
        }
        if (days >= 30) return Unit(days / 30, "month");
        if (days >= 1) return Unit(days, "day");
        return Unit(Math.Max(0, (int)age.TotalHours), "hour");
    }

    /// <summary>
    /// Every sub-action is also a script function with the same id ("twitch.ban" with { user, reason }). Missing
    /// arguments use the parameter defaults (resolved against the action's arguments). Returns the arguments the
    /// call wrote, so "get" functions return their results.
    /// </summary>
    public static IEnumerable<ScriptFunction> ScriptFunctions(Func<TwitchApi> api) => All.Select(spec =>
        new ScriptFunction(spec.TypeId, spec.DisplayName, spec.Parameters.Select(p => p.Key).ToArray(), async (context, json, ct) =>
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in spec.Parameters)
                values[parameter.Key] = json.GetElement(parameter.Key) is not null
                    ? json.GetString(parameter.Key)
                    : context.Resolve(parameter.DefaultValue);
            var before = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            await spec.Run(api(), context, new ParameterValues(values), ct).ConfigureAwait(false);
            var written = context.Args.Where(a => !before.TryGetValue(a.Key, out var old) || !Equals(old, a.Value))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase);
            return written.Count > 0 ? written : null;
        }));
}
