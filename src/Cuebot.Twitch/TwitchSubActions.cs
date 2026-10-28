using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Variables;
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
        new("twitch.clearChat", "Clear chat (clearSuccess)", [], async (api, c, _, ct) =>
        {
            await api.ClearChatAsync(ct).ConfigureAwait(false);
            c.Args["clearSuccess"] = true;
        }),
        new("twitch.getPresentViewers", "Get present viewers (presentViewerCount, presentViewers)", [],
            async (api, c, _, ct) =>
            {
                var chatters = await api.GetChattersAsync(ct).ConfigureAwait(false);
                c.Args["presentViewerCount"] = chatters.Count;
                c.Args["presentViewers"] = string.Join(",", chatters.Select(x => x.UserLogin));
            }),
        new("twitch.pinMessage", "Pin chat message",
            [
                new("messageId", "Message id") { DefaultValue = "%messageId%" },
                Number("duration", "Pinned for (seconds)", "0", "30 to 1800; 0 keeps it pinned until the stream ends"),
            ],
            (api, _, p, ct) => api.PinMessageAsync(p["messageId"], p.GetInt("duration"), ct)),
        new("twitch.unpinMessage", "Unpin chat message", [PinnedIdParam()],
            async (api, _, p, ct) => await api.UnpinMessageAsync(await PinnedIdAsync(api, p, ct).ConfigureAwait(false), ct).ConfigureAwait(false)),
        new("twitch.updatePinnedDuration", "Update pinned message duration",
            [PinnedIdParam(), Number("duration", "Pinned for (seconds, from now)", "60", "30 to 1800; 0 keeps it pinned until the stream ends")],
            async (api, _, p, ct) => await api.UpdatePinnedMessageAsync(await PinnedIdAsync(api, p, ct).ConfigureAwait(false), p.GetInt("duration"), ct)
                .ConfigureAwait(false)),
        new("twitch.getPinnedMessage", "Get pinned chat message (isPinned, pinnedMessage, pinnedMessageId, pinnedUser...)", [],
            async (api, c, _, ct) =>
            {
                var pin = await api.GetPinnedMessageAsync(ct).ConfigureAwait(false);
                c.Args["isPinned"] = pin is not null;
                c.Args["pinnedMessageId"] = pin?.MessageId ?? "";
                c.Args["pinnedMessage"] = pin?.Text ?? "";
                c.Args["pinnedUser"] = pin?.SenderUserName ?? "";
                c.Args["pinnedUserName"] = pin?.SenderUserLogin ?? "";
                c.Args["pinnedUserId"] = pin?.SenderUserId ?? "";
                c.Args["pinnedBy"] = pin?.PinnedByUserName ?? "";
                c.Args["pinnedAt"] = pin?.StartsAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
                c.Args["pinnedUntil"] = pin?.EndsAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
            }),

        // ---------------------------------------------------------------- channel
        new("twitch.setTitle", "Set channel title (titleSuccess)", [new("title", "Title")],
            async (api, c, p, ct) =>
            {
                await api.UpdateChannelAsync(new ChannelUpdate { Title = p["title"] }, ct).ConfigureAwait(false);
                c.Args["titleSuccess"] = true;
            }),
        new("twitch.setGame", "Set channel category (gameId, gameName, gameSuccess, gameBoxArt)", [new("game", "Category / game name")],
            async (api, c, p, ct) =>
            {
                var category = await api.FindCategoryAsync(p["game"], ct).ConfigureAwait(false)
                               ?? throw new TwitchApiException($"No Twitch category matches '{p["game"]}'.");
                await api.UpdateChannelAsync(new ChannelUpdate { GameId = category.Id }, ct).ConfigureAwait(false);
                c.Args["gameId"] = category.Id;
                c.Args["gameName"] = category.Name;
                c.Args["gameBoxArt"] = category.BoxArtUrl;
                c.Args["gameSuccess"] = true;
            }),
        new("twitch.setTags", "Set channel tags", [new("tags", "Tags") { Help = "Comma or space separated, at most 10" }],
            (api, _, p, ct) => api.UpdateChannelAsync(new ChannelUpdate { Tags = SplitTags(p["tags"]) }, ct)),
        new("twitch.addTag", "Add channel tag", [new("tag", "Tag")],
            (api, _, p, ct) => ChangeTagsAsync(api, tags => [.. tags, p["tag"]], ct)),
        new("twitch.removeTag", "Remove channel tag", [new("tag", "Tag")],
            (api, _, p, ct) => ChangeTagsAsync(api, tags => tags.Where(t => !t.Equals(p["tag"].Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase)).ToArray(), ct)),
        new("twitch.clearTags", "Clear channel tags", [],
            (api, _, _, ct) => api.UpdateChannelAsync(new ChannelUpdate { Tags = [] }, ct)),
        new("twitch.setBrandedContent", "Set branded content (success)", [Bool("enabled", "Branded content")],
            async (api, c, p, ct) =>
            {
                await api.UpdateChannelAsync(new ChannelUpdate { IsBrandedContent = p.GetBool("enabled") }, ct).ConfigureAwait(false);
                c.Args["success"] = true;
            }),
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
                c.Args["streamMarkerSuccess"] = true;
                c.Args["streamMarkerId"] = marker.Id;
                c.Args["streamMarkerCreatedAt"] = DateTime.Now;
                c.Args["streamMarkerDescription"] = marker.Description.Length > 0 ? marker.Description : p["description"];
                c.Args["streamMarkerPosition"] = marker.PositionSeconds;
            }),
        new("twitch.createClip", "Create clip (clipId, clipUrl, clipEditUrl)", [Bool("hasDelay", "Account for stream delay", false)],
            async (api, c, p, ct) =>
            {
                var clip = await api.CreateClipAsync(p.GetBool("hasDelay"), ct).ConfigureAwait(false);
                c.Args["clipId"] = clip.Id;
                c.Args["clipEditUrl"] = clip.EditUrl;
                c.Args["clipUrl"] = $"https://clips.twitch.tv/{clip.Id}";
                c.Args["createClipSuccess"] = true;
                c.Args["createClipId"] = clip.Id;
                c.Args["createClipCreatedAt"] = DateTime.Now.ToString(CultureInfo.CurrentCulture);
                c.Args["createClipUrl"] = $"https://clips.twitch.tv/{clip.Id}";
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
        new("twitch.snoozeAd", "Snooze next ad (snoozeCount, nextAdAt, snooze.success)", [],
            async (api, c, _, ct) =>
            {
                WriteAdSchedule(c, await api.SnoozeAdAsync(ct).ConfigureAwait(false));
                c.Args["snooze.success"] = true;
            }),
        new("twitch.getAdSchedule", "Get ad schedule (nextAdAt, prerollFreeTime, snoozeCount...)", [],
            async (api, c, _, ct) => WriteAdSchedule(c, await api.GetAdScheduleAsync(ct).ConfigureAwait(false))),

        // ---------------------------------------------------------------- raids and shoutouts
        new("twitch.startRaid", "Start raid (startRaidSuccess)", [UserParam("Channel to raid")],
            async (api, c, p, ct) =>
            {
                await api.StartRaidAsync(UserOrTrigger(c, p["user"]), ct).ConfigureAwait(false);
                c.Args["startRaidSuccess"] = true;
            }),
        new("twitch.cancelRaid", "Cancel raid", [], (api, _, _, ct) => api.CancelRaidAsync(ct)),
        new("twitch.sendShoutout", "Send shoutout (shoutoutSuccess)", [UserParam("Channel")],
            async (api, c, p, ct) =>
            {
                await api.SendShoutoutAsync(UserOrTrigger(c, p["user"]), ct).ConfigureAwait(false);
                c.Args["shoutoutSuccess"] = true;
            }),

        // ---------------------------------------------------------------- moderation
        new("twitch.ban", "Ban user (bannedUser, bannedUserName, bannedUserId, banResult)", [UserParam(), new("reason", "Reason")],
            (api, c, p, ct) => ModerateAsync(api, c, p, "bannedUser", "banResult", (id, t) => api.BanAsync(id, p["reason"], t), ct)),
        new("twitch.timeout", "Timeout user (timedOutUser, timedOutUserName, timedOutUserId, timeoutResult)",
            [UserParam(), Number("duration", "Duration (seconds)", "600", "1 second to 14 days"), new("reason", "Reason")],
            (api, c, p, ct) => ModerateAsync(api, c, p, "timedOutUser", "timeoutResult",
                (id, t) => api.TimeoutAsync(id, p.GetInt("duration", 600), p["reason"], t), ct)),
        new("twitch.unban", "Unban user (unbannedUser, unbannedUserName, unbannedUserId, unbanResult)", [UserParam()],
            (api, c, p, ct) => ModerateAsync(api, c, p, "unbannedUser", "unbanResult", api.UnbanAsync, ct)),
        new("twitch.untimeout", "Remove timeout (unTimedOutUser, unTimedOutUserName, unTimedOutUserId, unTimedOutResult)", [UserParam()],
            (api, c, p, ct) => ModerateAsync(api, c, p, "unTimedOutUser", "unTimedOutResult", api.UnbanAsync, ct)),
        new("twitch.warn", "Warn user (warnedUser, warnedUserName, warnedUserId, warnedResult)", [UserParam(), new("reason", "Reason")],
            (api, c, p, ct) => ModerateAsync(api, c, p, "warnedUser", "warnedResult", (id, t) => api.WarnAsync(id, p["reason"], t), ct)),
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
        new("twitch.createPoll", "Create poll (pollId, success)",
            [
                new("title", "Question"),
                new("choices", "Choices", ParameterKind.Multiline) { Help = "One per line (2 to 5)" },
                Number("duration", "Duration (seconds)", "60", "15 to 1800"),
                Number("pointsPerVote", "Channel points per extra vote", "0", "0 disables channel point voting"),
            ],
            async (api, c, p, ct) =>
            {
                c.Args["pollId"] = (await api.CreatePollAsync(p["title"], Lines(p["choices"]), p.GetInt("duration", 60),
                    p.GetInt("pointsPerVote"), ct).ConfigureAwait(false)).Id;
                c.Args["success"] = true;
            }),
        new("twitch.endPoll", "End active poll",
            [new("mode", "How", ParameterKind.Choice) { DefaultValue = "Terminate", Choices = ["Terminate", "Archive"], Help = "Archive hides the results" }],
            (api, _, p, ct) => api.EndPollAsync(p["mode"] == "Archive", null, ct)),
        new("twitch.getPoll", "Get latest poll (pollId, pollTitle, pollStatus, pollChoice0.., active, poll.*)", [],
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
                WritePoll(c, poll);
            }),

        // ---------------------------------------------------------------- predictions
        new("twitch.createPrediction", "Create prediction (predictionId, success, prediction.Id)",
            [
                new("title", "Question"),
                new("outcomes", "Outcomes", ParameterKind.Multiline) { Help = "One per line (2 to 10)" },
                Number("window", "Submission window (seconds)", "120", "30 to 1800"),
            ],
            async (api, c, p, ct) =>
            {
                var created = await api.CreatePredictionAsync(p["title"], Lines(p["outcomes"]), p.GetInt("window", 120), ct).ConfigureAwait(false);
                c.Args["predictionId"] = created.Id;
                c.Args["prediction.Id"] = created.Id;
                c.Args["success"] = true;
            }),
        new("twitch.lockPrediction", "Lock active prediction (locked, prediction.*)", [], async (api, c, _, ct) =>
        {
            WritePrediction(c, await api.LockPredictionAsync(ct).ConfigureAwait(false));
            c.Args["locked"] = true;
        }),
        new("twitch.cancelPrediction", "Cancel active prediction (refund) (cancelled, prediction.*)", [], async (api, c, _, ct) =>
        {
            WritePrediction(c, await api.CancelPredictionAsync(ct).ConfigureAwait(false));
            c.Args["cancelled"] = true;
        }),
        new("twitch.resolvePrediction", "Resolve prediction (success, prediction.*, prediction.winningOutcome.*)",
            [
                new("winner", "Winning outcome") { Help = "Outcome title, or its number (1, 2, ...)" },
                new("title", "Only if the prediction is titled") { Help = "Leave empty to resolve the latest prediction" },
            ],
            async (api, c, p, ct) =>
            {
                WritePrediction(c, await api.ResolvePredictionAsync(p["winner"], p["title"], ct).ConfigureAwait(false));
                c.Args["success"] = true;
            }),
        new("twitch.getPrediction", "Get latest prediction (predictionId, predictionTitle, predictionStatus, predictionOutcome0.., active, prediction.*)", [],
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
                WritePrediction(c, prediction);
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
                c.Args["rewardSkipRequestQueue"] = r.SkipQueue;
                c.Args["rewardMaxPerUserPerStream"] = r.MaxPerUserPerStream;
                c.Args["rewardGlobalCooldown"] = r.GlobalCooldownSeconds;
                c.Args["rewardIsOurs"] = r.IsManageable;
            }),
        new("twitch.updateRedemption", "Reward: fulfil or cancel redemption",
            [
                new("rewardId", "Reward id") { DefaultValue = "%rewardId%" },
                new("redemptionId", "Redemption id") { DefaultValue = "%redemptionId%" },
                new("status", "Status", ParameterKind.Choice) { DefaultValue = "Fulfil", Choices = ["Fulfil", "Cancel"], Help = "Cancel refunds the points" },
            ],
            (api, _, p, ct) => api.UpdateRedemptionAsync(p["rewardId"], p["redemptionId"], p["status"] != "Cancel", ct)),
        new("twitch.updateReward", "Reward: update title, prompt and cost",
            [
                RewardParam(),
                new("title", "Title") { Help = "Leave empty to keep" },
                new("prompt", "Prompt") { Help = "Leave empty to keep" },
                new("cost", "Cost") { Help = "Leave empty to keep; with an operator it changes the current cost" },
                new("operator", "Cost operator", ParameterKind.Choice) { DefaultValue = "Set", Choices = ["Set", "Add", "Subtract", "Multiply", "Divide"] },
            ],
            async (api, c, p, ct) =>
            {
                var reward = await RewardAsync(api, c, p, ct).ConfigureAwait(false);
                int? cost = double.TryParse(p["cost"], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                    ? ApplyCost(reward.Cost, amount, p["operator"])
                    : null;
                await api.UpdateRewardAsync(reward.Id, new RewardUpdate
                {
                    Title = p["title"].Trim().Length > 0 ? p["title"] : null,
                    Prompt = p["prompt"].Trim().Length > 0 ? p["prompt"] : null,
                    Cost = cost,
                }, ct).ConfigureAwait(false);
                c.Args["rewardCost"] = cost ?? reward.Cost;
            }),
        new("twitch.getRewardCounter", "Reward: get counters (counter, userCounter)", [RewardParam(), UserParam("For user")],
            async (api, c, p, ct) =>
            {
                var reward = await RewardAsync(api, c, p, ct).ConfigureAwait(false);
                var userId = p["user"].Trim().Length > 0 || c.Args.ContainsKey("userId") ? await UserIdAsync(api, c, p, ct).ConfigureAwait(false) : "";
                var (counter, userCounter) = api.Counters.GetRewardCounter(reward.Id, userId);
                c.Args["counter"] = counter;
                c.Args["userCounter"] = userCounter;
            }),
        new("twitch.resetRewardCounter", "Reward: reset counter", [RewardParam()],
            async (api, c, p, ct) => api.Counters.ResetRewardCounter((await RewardAsync(api, c, p, ct).ConfigureAwait(false)).Id)),
        new("twitch.resetRewardUserCounters", "Reward: reset user counters", [RewardParam()],
            async (api, c, p, ct) => api.Counters.ResetRewardUserCounters((await RewardAsync(api, c, p, ct).ConfigureAwait(false)).Id)),
        new("twitch.setRewardGroupEnabled", "Reward group: set enabled state (rewardsChanged)",
            [GroupParam(), StateParam("Enabled", "Disabled")],
            (api, c, p, ct) => SetGroupStateAsync(api, c, p["group"], p["state"], r => r.IsEnabled, on => new RewardUpdate { IsEnabled = on }, ct)),
        new("twitch.setRewardGroupPaused", "Reward group: set paused state (rewardsChanged)",
            [GroupParam(), StateParam("Paused", "Unpaused")],
            (api, c, p, ct) => SetGroupStateAsync(api, c, p["group"], p["state"], r => r.IsPaused, on => new RewardUpdate { IsPaused = on }, ct)),
        new("twitch.configureRewards", "Configure rewards (rewardsChanged)",
            [
                new("enable", "Enable", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
                new("disable", "Disable", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
                new("pause", "Pause", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
                new("unpause", "Unpause", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
                Bool("disableNotConfigured", "Disable rewards that are in no group", false),
            ],
            ConfigureRewardsAsync),

        // ---------------------------------------------------------------- users
        new("twitch.getUserInfo", "Get user info (targetUser, targetUserId, targetDescription, targetCreatedAt...)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var target = UserOrTrigger(c, p["user"]);
                var user = await api.GetUserAsync(target, ct).ConfigureAwait(false);
                c.Args["targetFound"] = user is not null;
                c.Args["addTargetResult"] = user is not null;
                if (user is null) return;
                c.Args["targetUser"] = user.DisplayName;
                c.Args["targetUserName"] = user.Login;
                c.Args["targetUserId"] = user.Id;
                c.Args["targetDescription"] = user.Description;
                c.Args["targetProfileImageUrl"] = user.ProfileImageUrl;
                c.Args["targetBroadcasterType"] = user.BroadcasterType;
                c.Args["targetCreatedAt"] = user.CreatedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
                c.Args["targetAccountAgeDays"] = user.CreatedAt is { } created ? (int)(DateTimeOffset.UtcNow - created).TotalDays : 0;
                var sub = await TrySubscriptionAsync(api, user.Id, ct).ConfigureAwait(false);
                var sbChannel = await api.GetChannelAsync(user.Id, ct).ConfigureAwait(false);
                if (sbChannel is { } channel)
                {
                    c.Args["targetGame"] = channel.GameName;
                    c.Args["targetTitle"] = channel.Title;
                }
                WriteTargetInfo(c, user, sbChannel, sub);
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
                if (follow is not null) WriteFollowAge(c, follow, DateTimeOffset.Now);
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
        new("twitch.getLatestSubscriber", "Get latest subscriber (latestSubscriber.userName, .userId, .tier, .isGift...)", [],
            (api, c, _, _) =>
            {
                var latest = api.Counters.LatestSubscriber;
                c.Args["latestSubscriber.userName"] = latest.UserName;
                c.Args["latestSubscriber.userLogin"] = latest.UserLogin;
                c.Args["latestSubscriber.userId"] = latest.UserId;
                c.Args["latestSubscriber.tier"] = latest.Tier.Length > 0 ? "Tier" + latest.Tier : "";
                c.Args["latestSubscriber.isGift"] = latest.IsGift;
                c.Args["latestSubscriber.at"] = latest.At?.ToString("o", CultureInfo.InvariantCulture) ?? "";
                return Task.CompletedTask;
            }),
        new("twitch.getSubCounter", "Get sub counter (subCounter.count, subCounter.rollover, subCounter.rolloverCount)", [],
            (api, c, _, _) =>
            {
                WriteSubCounter(c, api.Counters.SubCounter);
                return Task.CompletedTask;
            }),
        new("twitch.setSubCounter", "Set sub counter (setResult)",
            [Number("count", "Count", "0"), new("rolloverCount", "Rollover count") { Help = "Leave empty to keep" }],
            (api, c, p, _) =>
            {
                var valid = int.TryParse(p["count"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0;
                int? rollovers = int.TryParse(p["rolloverCount"].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;
                c.Args["setResult"] = valid;
                if (valid) WriteSubCounter(c, api.Counters.SetSubCount(count, rollovers));
                return Task.CompletedTask;
            }),
        new("twitch.getPresentUser", "Get present user by index (presentUser, presentUserId, presentUserName, presentUsersCount)",
            [new("index", "Index") { DefaultValue = "0", Help = "0 is the first present viewer; a variable such as %randomNumber% works" }],
            async (api, c, p, ct) =>
            {
                var chatters = await api.GetChattersAsync(ct).ConfigureAwait(false);
                var index = p.GetInt("index", -1);
                var found = index >= 0 && index < chatters.Count ? chatters[index] : null;
                c.Args["presentUsersCount"] = chatters.Count;
                c.Args["presentUser"] = found is null ? (object)false : found.UserName;
                c.Args["presentUserId"] = found?.UserId ?? "";
                c.Args["presentUserName"] = found?.UserLogin ?? "";
                c.Args["presentUserType"] = found is null ? "" : "twitch";
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
                // Streamer.bot names
                c.Args["broadcastUser"] = user?.DisplayName ?? login;
                c.Args["broadcastUserName"] = login;
                c.Args["broadcastUserId"] = id;
                c.Args["broadcastUserType"] = "twitch";
                c.Args["broadcastIsAffiliate"] = user?.BroadcasterType == "affiliate";
                c.Args["broadcastIsPartner"] = user?.BroadcasterType == "partner";
                c.Args["broadcasterProfileUrl"] = user?.ProfileImageUrl ?? "";
                c.Args["broadcasterOfflineUrl"] = user?.OfflineImageUrl ?? "";
                if (user?.CreatedAt is { } createdAt) c.Args["broadcasterCreatedAt"] = createdAt.LocalDateTime;
                c.Args["broadcasterDescription"] = user?.Description ?? "";
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
                    c.Args[$"randomUserType{i}"] = "twitch";
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

        // ---------------------------------------------------------------- guest star (Twitch beta)
        new("twitch.getGuestStarSettings", "Guest Star: get channel settings (guestStarSlotCount, guestStarLayout...)", [],
            async (api, c, _, ct) =>
            {
                var s = await api.GetGuestStarSettingsAsync(ct).ConfigureAwait(false);
                c.Args["guestStarSlotCount"] = s.SlotCount;
                c.Args["guestStarLayout"] = s.GroupLayout;
                c.Args["guestStarModeratorSendLive"] = s.IsModeratorSendLiveEnabled;
                c.Args["guestStarBrowserSourceAudio"] = s.IsBrowserSourceAudioEnabled;
                c.Args["guestStarBrowserSourceToken"] = s.BrowserSourceToken;
            }),
        new("twitch.updateGuestStarSettings", "Guest Star: update channel settings",
            [
                Number("slotCount", "Slots (1 to 6, 0 = keep)", "0"),
                new("layout", "Layout", ParameterKind.Choice)
                {
                    DefaultValue = "Keep", Choices = ["Keep", "TILED_LAYOUT", "SCREENSHARE_LAYOUT", "HORIZONTAL_LAYOUT", "VERTICAL_LAYOUT"],
                },
                Keep("moderatorSendLive", "Moderators can send guests live"),
                Keep("browserSourceAudio", "Browser source audio"),
                Bool("regenerateBrowserSources", "Regenerate browser sources (invalidates the old ones)", false),
            ],
            (api, _, p, ct) => api.UpdateGuestStarSettingsAsync(new GuestStarSettingsUpdate
            {
                SlotCount = p.GetInt("slotCount") > 0 ? p.GetInt("slotCount") : null,
                GroupLayout = p["layout"] is "" or "Keep" ? null : p["layout"],
                IsModeratorSendLiveEnabled = KeepValue(p["moderatorSendLive"]),
                IsBrowserSourceAudioEnabled = KeepValue(p["browserSourceAudio"]),
                RegenerateBrowserSources = p.GetBool("regenerateBrowserSources"),
            }, ct)),
        new("twitch.getGuestStarSession", "Guest Star: get session (guestStarSessionId, guestStarGuestCount, guestStarGuest0..)", [],
            async (api, c, _, ct) => WriteGuestStarSession(c, await api.GetGuestStarSessionAsync(ct).ConfigureAwait(false))),
        new("twitch.createGuestStarSession", "Guest Star: create session (guestStarSessionId)", [],
            async (api, c, _, ct) => WriteGuestStarSession(c, await api.CreateGuestStarSessionAsync(ct).ConfigureAwait(false))),
        new("twitch.endGuestStarSession", "Guest Star: end session", [SessionParam()],
            (api, _, p, ct) => api.EndGuestStarSessionAsync(p["sessionId"], ct)),
        new("twitch.getGuestStarInvites", "Guest Star: get invites (guestStarInviteCount, guestStarInvite0UserId, ...Status)", [SessionParam()],
            async (api, c, p, ct) =>
            {
                var invites = await api.GetGuestStarInvitesAsync(p["sessionId"], ct).ConfigureAwait(false);
                c.Args["guestStarInviteCount"] = invites.Count;
                c.Args["guestStarInvites"] = string.Join(",", invites.Select(i => i.UserId));
                for (var i = 0; i < invites.Count; i++)
                {
                    c.Args[$"guestStarInvite{i}UserId"] = invites[i].UserId;
                    c.Args[$"guestStarInvite{i}Status"] = invites[i].Status;
                }
            }),
        new("twitch.sendGuestStarInvite", "Guest Star: send invite", [UserParam("Guest"), SessionParam()],
            async (api, c, p, ct) => await api.SendGuestStarInviteAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["sessionId"], ct)
                .ConfigureAwait(false)),
        new("twitch.deleteGuestStarInvite", "Guest Star: delete invite", [UserParam("Guest"), SessionParam()],
            async (api, c, p, ct) => await api.DeleteGuestStarInviteAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["sessionId"], ct)
                .ConfigureAwait(false)),
        new("twitch.assignGuestStarSlot", "Guest Star: assign slot", [UserParam("Guest"), SlotParam("slot", "Slot"), SessionParam()],
            async (api, c, p, ct) => await api.AssignGuestStarSlotAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["slot"],
                p["sessionId"], ct).ConfigureAwait(false)),
        new("twitch.updateGuestStarSlot", "Guest Star: move guest to another slot",
            [SlotParam("slot", "From slot"), SlotParam("toSlot", "To slot"), SessionParam()],
            (api, _, p, ct) => api.MoveGuestStarSlotAsync(p["slot"], p["toSlot"], p["sessionId"], ct)),
        new("twitch.deleteGuestStarSlot", "Guest Star: remove guest from slot",
            [UserParam("Guest"), SlotParam("slot", "Slot"), Bool("reinvite", "Send back to the invite queue", false), SessionParam()],
            async (api, c, p, ct) => await api.RemoveGuestStarSlotAsync(await UserIdAsync(api, c, p, ct).ConfigureAwait(false), p["slot"],
                p.GetBool("reinvite"), p["sessionId"], ct).ConfigureAwait(false)),
        new("twitch.updateGuestStarSlotSettings", "Guest Star: update slot settings",
            [
                SlotParam("slot", "Slot"), Keep("audio", "Audio"), Keep("video", "Video"), Keep("live", "Live"),
                Number("volume", "Volume (0 to 100, -1 = keep)", "-1"), SessionParam(),
            ],
            (api, _, p, ct) => api.UpdateGuestStarSlotSettingsAsync(p["slot"], KeepValue(p["audio"]), KeepValue(p["video"]), KeepValue(p["live"]),
                p.GetInt("volume", -1) >= 0 ? p.GetInt("volume", -1) : null, p["sessionId"], ct)),
    ];

    private static ParameterDescriptor PinnedIdParam() =>
        new("messageId", "Pinned message id") { Help = "Leave empty for the message pinned now" };

    private static async Task<string> PinnedIdAsync(TwitchApi api, ParameterValues p, CancellationToken ct)
    {
        if (p["messageId"].Trim().Length > 0) return p["messageId"].Trim();
        return (await api.GetPinnedMessageAsync(ct).ConfigureAwait(false))?.MessageId ?? throw new TwitchApiException("No chat message is pinned.");
    }

    private static void WriteSubCounter(ActionRunContext c, SubCounterState s)
    {
        c.Args["subCounter.count"] = s.Count;
        c.Args["subCounter.rollover"] = s.Rollover;
        c.Args["subCounter.rolloverCount"] = s.RolloverCount;
    }

    // ---------------------------------------------------------------- reward helpers

    private static ParameterDescriptor GroupParam() => new("group", "Reward group") { Help = "Set a reward's group on the Twitch page (Rewards tab)" };

    private static ParameterDescriptor StateParam(string on, string off) =>
        new("state", "State", ParameterKind.Choice) { DefaultValue = on, Choices = [on, off, "Toggle"] };

    /// <summary>The new cost of a reward: "Set" replaces it, the other operators change the current cost (at least 1).</summary>
    internal static int ApplyCost(int current, double amount, string op) => (int)Math.Max(1, Math.Round(op switch
    {
        "Add" => current + amount,
        "Subtract" => current - amount,
        "Multiply" => current * amount,
        "Divide" => amount == 0 ? current : current / amount,
        _ => amount,
    }));

    /// <summary>
    /// Sets one flag on every reward of a group (Toggle: on unless any reward already has it). Only rewards Cuebot
    /// created can change; the others are counted in rewardsSkipped.
    /// </summary>
    private static async Task SetGroupStateAsync(TwitchApi api, ActionRunContext c, string group, string state, Func<TwitchReward, bool> flag,
        Func<bool, RewardUpdate> update, CancellationToken ct)
    {
        if (group.Trim().Length == 0) throw new ArgumentException("No reward group given.");
        var ids = api.Counters.RewardsInGroup(group).ToHashSet(StringComparer.Ordinal);
        var rewards = (await api.GetRewardsAsync(ct).ConfigureAwait(false)).Where(r => ids.Contains(r.Id)).ToArray();
        if (rewards.Length == 0) throw new TwitchApiException($"The reward group '{group.Trim()}' has no rewards.");
        var on = state == "Toggle" ? !rewards.Any(flag) : !state.StartsWith("Dis", StringComparison.Ordinal) && !state.StartsWith("Un", StringComparison.Ordinal);
        var changed = 0;
        foreach (var reward in rewards.Where(r => r.IsManageable && flag(r) != on))
        {
            await api.UpdateRewardAsync(reward.Id, update(on), ct).ConfigureAwait(false);
            changed++;
        }
        c.Args["rewardsChanged"] = changed;
        c.Args["rewardsSkipped"] = rewards.Count(r => !r.IsManageable);
    }

    /// <summary>Enables, disables, pauses and unpauses many rewards at once (by title, id or group), with one update each.</summary>
    private static async Task ConfigureRewardsAsync(TwitchApi api, ActionRunContext c, ParameterValues p, CancellationToken ct)
    {
        var rewards = await api.GetRewardsAsync(ct).ConfigureAwait(false);
        var updates = new Dictionary<string, RewardUpdate>(StringComparer.Ordinal);
        var mentioned = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<TwitchReward> Resolve(string list) => Lines(list).SelectMany(key =>
        {
            var inGroup = api.Counters.RewardsInGroup(key).ToHashSet(StringComparer.Ordinal);
            var matches = rewards.Where(r => r.Id == key || r.Title.Equals(key, StringComparison.OrdinalIgnoreCase) || inGroup.Contains(r.Id)).ToArray();
            return matches.Length > 0 ? matches : throw new TwitchApiException($"No reward or reward group '{key}' was found.");
        });

        void Apply(string list, Func<RewardUpdate, RewardUpdate> change)
        {
            foreach (var reward in Resolve(list))
            {
                mentioned.Add(reward.Id);
                updates[reward.Id] = change(updates.GetValueOrDefault(reward.Id) ?? new RewardUpdate());
            }
        }

        Apply(p["enable"], u => u with { IsEnabled = true });
        Apply(p["disable"], u => u with { IsEnabled = false });
        Apply(p["pause"], u => u with { IsPaused = true });
        Apply(p["unpause"], u => u with { IsPaused = false });
        if (p.GetBool("disableNotConfigured"))
            foreach (var reward in rewards.Where(r => !mentioned.Contains(r.Id) && r.IsEnabled && api.Counters.GetGroup(r.Id).Length == 0))
                updates[reward.Id] = new RewardUpdate { IsEnabled = false };

        var changed = 0;
        foreach (var reward in rewards.Where(r => r.IsManageable && updates.ContainsKey(r.Id)))
        {
            await api.UpdateRewardAsync(reward.Id, updates[reward.Id], ct).ConfigureAwait(false);
            changed++;
        }
        c.Args["rewardsChanged"] = changed;
        c.Args["rewardsSkipped"] = rewards.Count(r => !r.IsManageable && updates.ContainsKey(r.Id));
    }

    // ---------------------------------------------------------------- guest star helpers

    private static ParameterDescriptor SessionParam() =>
        new("sessionId", "Session id") { Help = "Leave empty for the active Guest Star session" };

    private static ParameterDescriptor SlotParam(string key, string label) =>
        new(key, label) { DefaultValue = "1", Help = "Slot number, 1 to the number of slots" };

    private static ParameterDescriptor Keep(string key, string label) =>
        new(key, label, ParameterKind.Choice) { DefaultValue = "Keep", Choices = ["Keep", "On", "Off"] };

    private static bool? KeepValue(string value) => value switch
    {
        "On" or "true" => true,
        "Off" or "false" => false,
        _ => null,
    };

    private static void WriteGuestStarSession(ActionRunContext c, TwitchGuestStarSession? session)
    {
        var guests = session?.Guests ?? [];
        c.Args["guestStarActive"] = session is not null;
        c.Args["guestStarSessionId"] = session?.Id ?? "";
        c.Args["guestStarGuestCount"] = guests.Count;
        for (var i = 0; i < guests.Count; i++)
        {
            c.Args[$"guestStarGuest{i}"] = guests[i].UserDisplayName;
            c.Args[$"guestStarGuest{i}UserName"] = guests[i].UserLogin;
            c.Args[$"guestStarGuest{i}UserId"] = guests[i].UserId;
            c.Args[$"guestStarGuest{i}Slot"] = guests[i].SlotId;
            c.Args[$"guestStarGuest{i}IsLive"] = guests[i].IsLive;
            c.Args[$"guestStarGuest{i}Volume"] = guests[i].Volume;
        }
    }

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

    /// <summary>
    /// Runs a moderation call on the target user and writes Streamer.bot's outputs: {prefix} (display name),
    /// {prefix}Name, {prefix}Id, {prefix}Type and {result} (true; a failure stops the action instead).
    /// </summary>
    private static async Task ModerateAsync(TwitchApi api, ActionRunContext c, ParameterValues p, string prefix, string result,
        Func<string, CancellationToken, Task> run, CancellationToken ct)
    {
        string Arg(string key) => c.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        string id, login, name;
        var target = p["user"].Trim().Length == 0 && Arg("userId").Length > 0 && Arg("userName").Length > 0 ? null : UserOrTrigger(c, p["user"]);
        if (target is null)
        {
            // The triggering user: no lookup needed.
            (id, login, name) = (Arg("userId"), Arg("userName"), Arg("user").Length > 0 ? Arg("user") : Arg("userName"));
            await run(id, ct).ConfigureAwait(false);
        }
        else if (TwitchApi.UserQuery(target) is ("id", var byId))
        {
            // An id needs no lookup; the names come from the trigger when it is the same user, else stay empty.
            id = byId;
            await run(id, ct).ConfigureAwait(false);
            (login, name) = Arg("userId") == id ? (Arg("userName"), Arg("user")) : ("", "");
        }
        else
        {
            var user = await api.GetUserAsync(target, ct).ConfigureAwait(false)
                       ?? throw new TwitchApiException($"Twitch user '{target}' was not found.");
            (id, login, name) = (user.Id, user.Login, user.DisplayName);
            await run(id, ct).ConfigureAwait(false);
        }
        c.Args[prefix] = name;
        c.Args[prefix + "Name"] = login;
        c.Args[prefix + "Id"] = id;
        c.Args[prefix + "Type"] = "twitch";
        c.Args[result] = true;
    }

    /// <summary>Get Active Poll's outputs: active and poll.* (choices numbered from 0).</summary>
    internal static void WritePoll(ActionRunContext c, TwitchPoll? poll)
    {
        c.Args["active"] = poll?.Status == "ACTIVE";
        if (poll is null) return;
        c.Args["poll.Title"] = poll.Title;
        c.Args["poll.Id"] = poll.Id;
        c.Args["poll.Duration"] = poll.Duration;
        if (poll.StartedAt is { } started)
        {
            c.Args["poll.StartedAt"] = started.LocalDateTime;
            c.Args["poll.EndsAt"] = started.AddSeconds(poll.Duration).LocalDateTime;
            c.Args["poll.DurationRemaining"] = poll.Status == "ACTIVE"
                ? (int)Math.Max(0, Math.Round((started.AddSeconds(poll.Duration) - DateTimeOffset.UtcNow).TotalSeconds))
                : 0;
        }
        c.Args["poll.choices.count"] = poll.Choices.Count;
        int votes = 0, rewardVotes = 0;
        for (var i = 0; i < poll.Choices.Count; i++)
        {
            var choice = poll.Choices[i];
            c.Args[$"poll.choice{i}.title"] = choice.Title;
            c.Args[$"poll.choice{i}.id"] = choice.Id;
            c.Args[$"poll.choice{i}.votes"] = choice.Votes - choice.ChannelPointsVotes;
            c.Args[$"poll.choice{i}.rewardVotes"] = choice.ChannelPointsVotes;
            c.Args[$"poll.choice{i}.totalVotes"] = choice.Votes;
            votes += choice.Votes - choice.ChannelPointsVotes;
            rewardVotes += choice.ChannelPointsVotes;
        }
        c.Args["poll.votes"] = votes;
        c.Args["poll.rewardVotes"] = rewardVotes;
        c.Args["poll.totalVotes"] = votes + rewardVotes;
    }

    /// <summary>The prediction sub-actions' outputs: active and prediction.* (outcomes numbered from 0, winner when resolved).</summary>
    internal static void WritePrediction(ActionRunContext c, TwitchPrediction? prediction)
    {
        c.Args["active"] = prediction?.Status == "ACTIVE";
        if (prediction is null) return;
        c.Args["prediction.Id"] = prediction.Id;
        c.Args["prediction.Title"] = prediction.Title;
        c.Args["prediction.PredictionWindow"] = prediction.PredictionWindow;
        if (prediction.CreatedAt is { } created) c.Args["prediction.CreatedAt"] = created.LocalDateTime;
        for (var i = 0; i < prediction.Outcomes.Count; i++)
        {
            var o = prediction.Outcomes[i];
            var prefixes = new List<string> { $"prediction.outcome{i}." };
            if (prediction.WinningOutcomeId.Length > 0 && o.Id == prediction.WinningOutcomeId)
            {
                c.Args["prediction.winningIndex"] = i;
                prefixes.Add("prediction.winningOutcome.");
                // The docs spell the title "prediction.winningCutcome.title"; set both spellings.
                c.Args["prediction.winningCutcome.title"] = o.Title;
            }
            foreach (var p in prefixes)
            {
                c.Args[p + "id"] = o.Id;
                c.Args[p + "title"] = o.Title;
                c.Args[p + "users"] = o.Users;
                c.Args[p + "points"] = o.ChannelPoints;
                c.Args[p + "color"] = o.Color;
            }
        }
    }

    /// <summary>Get User Info for Target's Streamer.bot outputs (target*, game, gameId, createdAt, accountAge, tags).</summary>
    private static void WriteTargetInfo(ActionRunContext c, TwitchUser user, TwitchChannelInfo? channel, (bool Known, TwitchSubscription? Sub) sub)
    {
        c.Args["targetDescriptionEscaped"] = StreamerBotArgs.UrlEncode(user.Description);
        c.Args["targetUserProfileImageUrl"] = user.ProfileImageUrl;
        c.Args["targetUserProfileImageUrlEscaped"] = StreamerBotArgs.UrlEncode(user.ProfileImageUrl);
        c.Args["targetUserType"] = user.BroadcasterType;
        c.Args["targetIsAffiliate"] = user.BroadcasterType == "affiliate";
        c.Args["targetIsPartner"] = user.BroadcasterType == "partner";
        if (user.CreatedAt is { } created)
        {
            c.Args["createdAt"] = created.LocalDateTime;
            c.Args["accountAge"] = (long)(DateTimeOffset.UtcNow - created).TotalSeconds;
        }
        if (channel is not null)
        {
            c.Args["targetChannelTitle"] = channel.Title;
            c.Args["game"] = channel.GameName;
            c.Args["gameId"] = channel.GameId;
            c.Args["tagCount"] = channel.Tags.Count;
            c.Args["tags"] = channel.Tags.ToList();
            c.Args["tagsDelimited"] = string.Join(",", channel.Tags);
            for (var i = 0; i < channel.Tags.Count; i++) c.Args[$"tag{i}"] = channel.Tags[i];
        }
        if (!sub.Known) return;
        c.Args["targetIsSubscribed"] = sub.Sub is not null;
        c.Args["targetSubscriptionTier"] = sub.Sub is null ? "" : sub.Sub.Tier + "000";
    }

    /// <summary>The user's subscription, or not known (missing channel:read:subscriptions).</summary>
    private static async Task<(bool Known, TwitchSubscription? Sub)> TrySubscriptionAsync(TwitchApi api, string userId, CancellationToken ct)
    {
        try
        {
            return (true, await api.GetSubscriptionAsync(userId, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is TwitchApiException or InvalidOperationException)
        {
            return (false, null);
        }
    }

    /// <summary>Get Follow Age Info's Streamer.bot outputs (followDate, followAgeLong/Short, followAge.years...).</summary>
    internal static void WriteFollowAge(ActionRunContext c, TwitchFollow follow, DateTimeOffset now)
    {
        var since = follow.FollowedAt.ToLocalTime();
        var age = now - since;
        c.Args["followDate"] = since.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        c.Args["followUser"] = follow.UserName;
        c.Args["followUserName"] = follow.UserLogin;
        c.Args["followUserId"] = follow.UserId;
        c.Args["followAgeMinutes"] = ((long)age.TotalMinutes).ToString("N0", CultureInfo.InvariantCulture);
        c.Args["followAgeSeconds"] = ((long)age.TotalSeconds).ToString("N0", CultureInfo.InvariantCulture);

        // Calendar parts: whole years, then months, days, hours, minutes and seconds left over.
        var cursor = since;
        var years = 0;
        while (cursor.AddYears(years + 1) <= now) years++;
        cursor = cursor.AddYears(years);
        var months = 0;
        while (cursor.AddMonths(months + 1) <= now) months++;
        cursor = cursor.AddMonths(months);
        var rest = now - cursor;
        int[] parts = [years, months, rest.Days, rest.Hours, rest.Minutes, rest.Seconds];
        string[] names = ["year", "month", "day", "hour", "minute", "second"];
        string[] shorts = ["y", "m", "d", "h", "m", "s"];
        string[] keys = ["years", "months", "days", "hours", "minutes", "seconds"];
        var longText = new List<string>();
        var shortText = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            c.Args["followAge." + keys[i]] = parts[i];
            if (parts[i] == 0) continue;
            longText.Add($"{parts[i]} {names[i]}{(parts[i] == 1 ? "" : "s")}");
            shortText.Add($"{parts[i]}{shorts[i]}");
        }
        c.Args["followAgeLong"] = string.Join(", ", longText);
        c.Args["followAgeShort"] = string.Join(", ", shortText);
    }

    private static void WriteAdSchedule(ActionRunContext c, TwitchAdSchedule s)
    {
        c.Args["nextAdAt"] = s.NextAdAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        c.Args["lastAdAt"] = s.LastAdAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        c.Args["adDuration"] = s.Duration;
        c.Args["prerollFreeTime"] = s.PrerollFreeTime;
        c.Args["snoozeCount"] = s.SnoozeCount;
        c.Args["snoozeRefreshAt"] = s.SnoozeRefreshAt?.ToString("o", CultureInfo.InvariantCulture) ?? "";
        c.Args["prerollFree"] = s.PrerollFreeTime > 0;
        c.Args["prerollFreeSeconds"] = s.PrerollFreeTime;
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
