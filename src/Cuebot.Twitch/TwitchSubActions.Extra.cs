using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Twitch;

/// <summary>Sub-actions for the Helix endpoints added from the 2026 Twitch API reference.</summary>
internal static partial class TwitchSubActions
{
    private static readonly IReadOnlyList<string> LeaderboardPeriods = ["all", "day", "week", "month", "year"];
    private static readonly IReadOnlyList<string> UnbanStatuses = ["pending", "approved", "denied", "acknowledged", "canceled"];
    private static readonly IReadOnlyList<string> VideoTypes = ["archive", "highlight", "upload", "all"];

    private static readonly Dictionary<string, string> AutomodLevels = new()
    {
        ["0"] = "0: off", ["1"] = "1: less filtering", ["2"] = "2", ["3"] = "3", ["4"] = "4: most filtering",
    };

    private static string Iso(DateTimeOffset? time) => time?.ToString("o", CultureInfo.InvariantCulture) ?? "";

    private static IReadOnlyList<TwitchSubActionSpec> Extra { get; } =
    [
        // ---------------------------------------------------------------- clips and videos
        new("twitch.createClipFromVod", "Clip a past moment (clipId, clipUrl, clipEditUrl)",
            [
                new("title", "Title") { DefaultValue = "Clip", Help = "Twitch requires a title for these clips" },
                Number("secondsAgo", "Ends how many seconds ago", "0", "With no video id: where the clip ends, counted back from now in the current broadcast"),
                Number("duration", "Length (seconds)", "30", "5 to 60"),
                new("videoId", "Video id") { Help = "Leave empty for the current (or latest) broadcast. Needs past broadcasts stored on Twitch." },
                Number("endOffset", "Ends at (seconds into the video)", "0", "With a video id; 0 uses 'Ends how many seconds ago'"),
            ],
            async (api, c, p, ct) =>
            {
                var videoId = p["videoId"].Trim();
                var offset = p.GetInt("endOffset");
                if (videoId.Length == 0 || offset <= 0)
                {
                    TwitchVideo? video = videoId.Length == 0
                        ? (await api.GetVideosAsync(null, "archive", 1, ct).ConfigureAwait(false)).FirstOrDefault()
                        : null;
                    if (videoId.Length == 0)
                    {
                        if (video is null)
                            throw new InvalidOperationException("The channel has no past broadcast to clip; turn on \"Store past broadcasts\" on Twitch.");
                        videoId = video.Id;
                    }
                    if (offset <= 0)
                    {
                        if (video?.CreatedAt is not { } started)
                            throw new ArgumentException("Give the end as seconds into the video when clipping a video by id.");
                        offset = (int)(DateTimeOffset.UtcNow - started).TotalSeconds - Math.Max(0, p.GetInt("secondsAgo"));
                    }
                }
                var clip = await api.CreateClipFromVodAsync(videoId, offset, p["title"], p.GetDouble("duration", 30), ct).ConfigureAwait(false);
                c.Args["clipId"] = clip.Id;
                c.Args["clipEditUrl"] = clip.EditUrl;
                c.Args["clipUrl"] = $"https://clips.twitch.tv/{clip.Id}";
                c.Args["clipVideoId"] = videoId;
            }),
        new("twitch.getVideos", "Get videos (videoCount, video0.title, video0.url, videoTitle, videoUrl...)",
            [
                UserParam("Channel") with { Help = "Login or id:12345; leave empty for your own channel" },
                new("type", "Type", ParameterKind.Choice) { DefaultValue = "archive", Choices = VideoTypes },
                Number("count", "How many", "1", "1 to 100, newest first"),
            ],
            async (api, c, p, ct) =>
            {
                var videos = await api.GetVideosAsync(p["user"], p["type"], p.GetInt("count", 1), ct).ConfigureAwait(false);
                c.Args["videoCount"] = videos.Count;
                for (var i = 0; i < videos.Count; i++) WriteVideo(c, $"video{i}.", videos[i]);
                if (videos.Count > 0) WriteVideo(c, "video", videos[0], flat: true);
            }),
        new("twitch.searchChannels", "Search channels (channelCount, channel0.login, channel0.isLive, channel0.gameName...)",
            [new("query", "Search for"), Bool("liveOnly", "Only live channels", false), Number("count", "How many", "5", "1 to 100")],
            async (api, c, p, ct) =>
            {
                var found = await api.SearchChannelsAsync(p["query"], p.GetBool("liveOnly"), p.GetInt("count", 5), ct).ConfigureAwait(false);
                c.Args["channelCount"] = found.Count;
                for (var i = 0; i < found.Count; i++)
                {
                    var f = found[i];
                    c.Args[$"channel{i}.id"] = f.Id;
                    c.Args[$"channel{i}.login"] = f.Login;
                    c.Args[$"channel{i}.name"] = f.DisplayName;
                    c.Args[$"channel{i}.isLive"] = f.IsLive;
                    c.Args[$"channel{i}.title"] = f.Title;
                    c.Args[$"channel{i}.gameName"] = f.GameName;
                    c.Args[$"channel{i}.startedAt"] = Iso(f.StartedAt);
                }
            }),
        new("twitch.getStreamMarkers", "Get stream markers of the current broadcast (markerCount, marker0.description, marker0.position...)",
            [Number("count", "How many", "20", "1 to 100")],
            async (api, c, p, ct) =>
            {
                var markers = await api.GetStreamMarkersAsync(p.GetInt("count", 20), ct).ConfigureAwait(false);
                c.Args["markerCount"] = markers.Count;
                for (var i = 0; i < markers.Count; i++)
                {
                    c.Args[$"marker{i}.id"] = markers[i].Id;
                    c.Args[$"marker{i}.description"] = markers[i].Description;
                    c.Args[$"marker{i}.position"] = markers[i].PositionSeconds;
                    c.Args[$"marker{i}.url"] = markers[i].Url;
                }
            }),

        // ---------------------------------------------------------------- hype train, goals, bits, power-ups, charity
        new("twitch.getHypeTrain", "Get hype train status (hypeTrainActive, hypeTrainLevel, hypeTrainProgress, hypeTrainGoal, hypeTrainRecordLevel...)", [],
            async (api, c, _, ct) =>
            {
                var status = await api.GetHypeTrainStatusAsync(ct).ConfigureAwait(false);
                var t = status.Current;
                c.Args["hypeTrainActive"] = t is not null;
                c.Args["hypeTrainId"] = t?.Id ?? "";
                c.Args["hypeTrainType"] = t?.Type ?? "";
                c.Args["hypeTrainLevel"] = t?.Level ?? 0;
                c.Args["hypeTrainTotal"] = t?.Total ?? 0;
                c.Args["hypeTrainProgress"] = t?.Progress ?? 0;
                c.Args["hypeTrainGoal"] = t?.Goal ?? 0;
                c.Args["hypeTrainPercent"] = t is { Goal: > 0 } ? (int)Math.Round(100.0 * t.Progress / t.Goal) : 0;
                c.Args["hypeTrainExpiresAt"] = Iso(t?.ExpiresAt);
                c.Args["hypeTrainIsShared"] = t?.IsShared ?? false;
                var top = t?.TopContributions.OrderByDescending(x => x.Total).FirstOrDefault();
                c.Args["hypeTrainTopContributor"] = top?.UserName ?? "";
                c.Args["hypeTrainTopContribution"] = top?.Total ?? 0;
                c.Args["hypeTrainRecordLevel"] = status.RecordLevel;
                c.Args["hypeTrainRecordTotal"] = status.RecordTotal;
                c.Args["hypeTrainRecordAt"] = Iso(status.RecordAchievedAt);
            }),
        new("twitch.getCreatorGoals", "Get creator goals (goalCount, goal0.type, goal0.current, goal0.target, goal0.description...)", [],
            async (api, c, _, ct) =>
            {
                var goals = await api.GetCreatorGoalsAsync(ct).ConfigureAwait(false);
                c.Args["goalCount"] = goals.Count;
                for (var i = 0; i < goals.Count; i++)
                {
                    c.Args[$"goal{i}.id"] = goals[i].Id;
                    c.Args[$"goal{i}.type"] = goals[i].Type;
                    c.Args[$"goal{i}.description"] = goals[i].Description;
                    c.Args[$"goal{i}.current"] = goals[i].CurrentAmount;
                    c.Args[$"goal{i}.target"] = goals[i].TargetAmount;
                }
            }),
        new("twitch.getBitsLeaderboard", "Get bits leaderboard (leaderCount, leader0.user, leader0.score, leader0.rank...)",
            [
                Number("count", "How many", "10", "1 to 100"),
                new("period", "Period", ParameterKind.Choice) { DefaultValue = "all", Choices = LeaderboardPeriods },
            ],
            async (api, c, p, ct) =>
            {
                var leaders = await api.GetBitsLeaderboardAsync(p.GetInt("count", 10), p["period"], ct).ConfigureAwait(false);
                c.Args["leaderCount"] = leaders.Count;
                for (var i = 0; i < leaders.Count; i++)
                {
                    c.Args[$"leader{i}.rank"] = leaders[i].Rank;
                    c.Args[$"leader{i}.user"] = leaders[i].UserName;
                    c.Args[$"leader{i}.userName"] = leaders[i].UserLogin;
                    c.Args[$"leader{i}.userId"] = leaders[i].UserId;
                    c.Args[$"leader{i}.score"] = leaders[i].Score;
                }
            }),
        new("twitch.getCustomPowerUps", "Get custom power-ups (powerUpCount, powerUp0.id, powerUp0.title, powerUp0.bits...)", [],
            async (api, c, _, ct) =>
            {
                var powerUps = await api.GetCustomPowerUpsAsync(ct).ConfigureAwait(false);
                c.Args["powerUpCount"] = powerUps.Count;
                for (var i = 0; i < powerUps.Count; i++)
                {
                    c.Args[$"powerUp{i}.id"] = powerUps[i].Id;
                    c.Args[$"powerUp{i}.title"] = powerUps[i].Title;
                    c.Args[$"powerUp{i}.bits"] = powerUps[i].Bits;
                    c.Args[$"powerUp{i}.enabled"] = powerUps[i].IsEnabled;
                    c.Args[$"powerUp{i}.paused"] = powerUps[i].IsPaused;
                    c.Args[$"powerUp{i}.image"] = powerUps[i].ImageUrl;
                }
            }),
        new("twitch.getCharityDonations", "Get charity donations (donationCount, donation0.user, donation0.amount, donation0.currency...)",
            [Number("count", "How many", "20", "1 to 100")],
            async (api, c, p, ct) =>
            {
                var donations = await api.GetCharityDonationsAsync(p.GetInt("count", 20), ct).ConfigureAwait(false);
                c.Args["donationCount"] = donations.Count;
                for (var i = 0; i < donations.Count; i++)
                {
                    c.Args[$"donation{i}.user"] = donations[i].UserName;
                    c.Args[$"donation{i}.userName"] = donations[i].UserLogin;
                    c.Args[$"donation{i}.amount"] = donations[i].Amount;
                    c.Args[$"donation{i}.currency"] = donations[i].Currency;
                }
            }),

        // ---------------------------------------------------------------- moderation
        new("twitch.getUnbanRequests", "Get unban requests (unbanRequestCount, unbanRequest0.id, unbanRequest0.user, unbanRequest0.text...)",
            [new("status", "Status", ParameterKind.Choice) { DefaultValue = "pending", Choices = UnbanStatuses }],
            async (api, c, p, ct) =>
            {
                var requests = await api.GetUnbanRequestsAsync(p["status"], ct).ConfigureAwait(false);
                c.Args["unbanRequestCount"] = requests.Count;
                for (var i = 0; i < requests.Count; i++)
                {
                    c.Args[$"unbanRequest{i}.id"] = requests[i].Id;
                    c.Args[$"unbanRequest{i}.user"] = requests[i].UserName;
                    c.Args[$"unbanRequest{i}.userName"] = requests[i].UserLogin;
                    c.Args[$"unbanRequest{i}.userId"] = requests[i].UserId;
                    c.Args[$"unbanRequest{i}.text"] = requests[i].Text;
                    c.Args[$"unbanRequest{i}.createdAt"] = Iso(requests[i].CreatedAt);
                }
            }),
        new("twitch.resolveUnbanRequest", "Approve or deny an unban request",
            [
                new("requestId", "Unban request id") { DefaultValue = "%requestId%", Help = "From the Unban request created trigger or Get unban requests" },
                new("decision", "Decision", ParameterKind.Choice) { DefaultValue = "deny", Choices = ["approve", "deny"] },
                new("message", "Message to the user") { Help = "Optional, up to 500 characters" },
            ],
            async (api, c, p, ct) =>
            {
                var resolved = await api.ResolveUnbanRequestAsync(p["requestId"], p["decision"] == "approve", p["message"], ct).ConfigureAwait(false);
                c.Args["unbanRequestStatus"] = resolved?.Status ?? (p["decision"] == "approve" ? "approved" : "denied");
            }),
        new("twitch.getBanStatus", "Get ban status (isBanned, isTimedOut, banReason, timeoutEndsAt, bannedBy)", [UserParam()],
            async (api, c, p, ct) =>
            {
                var ban = (await api.GetBannedUsersAsync(UserOrTrigger(c, p["user"]), ct).ConfigureAwait(false)).FirstOrDefault();
                c.Args["isBanned"] = ban is { IsTimeout: false };
                c.Args["isTimedOut"] = ban is { IsTimeout: true };
                c.Args["banReason"] = ban?.Reason ?? "";
                c.Args["bannedAt"] = Iso(ban?.BannedAt);
                c.Args["timeoutEndsAt"] = Iso(ban?.ExpiresAt);
                c.Args["bannedBy"] = ban?.ModeratorName ?? "";
            }),
        new("twitch.getAutomodSettings", "Get AutoMod settings (automodLevel, automodAggression, automodBullying, automodSwearing...)", [],
            async (api, c, _, ct) => WriteAutomod(c, await api.GetAutomodSettingsAsync(ct).ConfigureAwait(false))),
        new("twitch.setAutomodLevel", "Set AutoMod level",
            [new("level", "Level", ParameterKind.Choice) { DefaultValue = "1", Choices = ["0", "1", "2", "3", "4"], ChoiceLabels = AutomodLevels }],
            async (api, c, p, ct) => WriteAutomod(c, await api.SetAutomodLevelAsync(p.GetInt("level", 1), ct).ConfigureAwait(false))),
        new("twitch.blockUser", "Block user (for the broadcaster account)", [UserParam()],
            (api, c, p, ct) => api.BlockUserAsync(UserOrTrigger(c, p["user"]), ct)),
        new("twitch.unblockUser", "Unblock user", [UserParam()],
            (api, c, p, ct) => api.UnblockUserAsync(UserOrTrigger(c, p["user"]), ct)),

        // ---------------------------------------------------------------- schedule
        new("twitch.getSchedule", "Get stream schedule (nextStreamAt, nextStreamTitle, nextStreamCategory, scheduleCount, schedule0.title...)",
            [Number("count", "How many", "5", "1 to 25")],
            async (api, c, p, ct) =>
            {
                var schedule = await api.GetScheduleAsync(p.GetInt("count", 5), ct).ConfigureAwait(false);
                var upcoming = schedule.Segments.Where(s => !s.IsCanceled).ToArray();
                c.Args["scheduleCount"] = upcoming.Length;
                for (var i = 0; i < upcoming.Length; i++)
                {
                    c.Args[$"schedule{i}.id"] = upcoming[i].Id;
                    c.Args[$"schedule{i}.title"] = upcoming[i].Title;
                    c.Args[$"schedule{i}.start"] = Iso(upcoming[i].Start);
                    c.Args[$"schedule{i}.end"] = Iso(upcoming[i].End);
                    c.Args[$"schedule{i}.category"] = upcoming[i].CategoryName;
                    c.Args[$"schedule{i}.isRecurring"] = upcoming[i].IsRecurring;
                }
                var next = upcoming.FirstOrDefault();
                c.Args["nextStreamAt"] = Iso(next?.Start);
                c.Args["nextStreamAtLocal"] = next?.Start?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) ?? "";
                c.Args["nextStreamTitle"] = next?.Title ?? "";
                c.Args["nextStreamCategory"] = next?.CategoryName ?? "";
                c.Args["vacationStart"] = Iso(schedule.VacationStart);
                c.Args["vacationEnd"] = Iso(schedule.VacationEnd);
                c.Args["onVacation"] = schedule.VacationStart is { } vs && schedule.VacationEnd is { } ve && vs <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow < ve;
            }),
        new("twitch.addScheduleSegment", "Add to stream schedule (scheduleSegmentId)",
            [
                new("start", "Starts at") { Help = "A date and time, like 2026-12-01 19:00 (this computer's time zone)" },
                Number("duration", "Length (minutes)", "180", "30 to 1380"),
                new("title", "Title"),
                new("category", "Category") { Help = "Category name or id; leave empty for none" },
                Bool("recurring", "Repeat every week", false),
            ],
            async (api, c, p, ct) =>
            {
                if (!DateTimeOffset.TryParse(p["start"], CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var start))
                    throw new ArgumentException($"\"{p["start"]}\" is not a date and time.");
                c.Args["scheduleSegmentId"] = await api.AddScheduleSegmentAsync(start, p.GetInt("duration", 180), p["title"], p["category"],
                    p.GetBool("recurring"), null, ct).ConfigureAwait(false);
            }),
        new("twitch.cancelScheduleSegment", "Cancel or restore a scheduled stream",
            [
                new("segmentId", "Schedule segment id") { DefaultValue = "%schedule0.id%" },
                Bool("canceled", "Cancelled", true),
            ],
            (api, _, p, ct) => api.SetScheduleSegmentCanceledAsync(p["segmentId"], p.GetBool("canceled", true), ct)),
        new("twitch.deleteScheduleSegment", "Remove from stream schedule",
            [new("segmentId", "Schedule segment id") { DefaultValue = "%scheduleSegmentId%" }],
            (api, _, p, ct) => api.DeleteScheduleSegmentAsync(p["segmentId"], ct)),
        new("twitch.setVacation", "Set schedule vacation",
            [
                Bool("enabled", "On vacation", true),
                new("start", "From") { Help = "Date and time; needed when turning vacation on" },
                new("end", "Until") { Help = "Date and time; needed when turning vacation on" },
            ],
            (api, _, p, ct) =>
            {
                DateTimeOffset? Parse(string key) =>
                    DateTimeOffset.TryParse(p[key], CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
                return api.SetVacationAsync(p.GetBool("enabled", true), Parse("start"), Parse("end"), null, ct);
            }),

        // ---------------------------------------------------------------- chat
        new("twitch.getSharedChatSession", "Get shared chat session (sharedChatActive, sharedChatHostId, sharedChatParticipantCount...)", [],
            async (api, c, _, ct) =>
            {
                var session = await api.GetSharedChatSessionAsync(ct).ConfigureAwait(false);
                c.Args["sharedChatActive"] = session is not null;
                c.Args["sharedChatSessionId"] = session?.SessionId ?? "";
                c.Args["sharedChatHostId"] = session?.HostBroadcasterId ?? "";
                c.Args["sharedChatIsHost"] = session is not null && session.HostBroadcasterId == api.Broadcaster.UserId;
                c.Args["sharedChatParticipantCount"] = session?.ParticipantIds.Count ?? 0;
                c.Args["sharedChatParticipantIds"] = session is null ? "" : string.Join(",", session.ParticipantIds);
            }),
    ];

    private static void WriteVideo(ActionRunContext c, string prefix, TwitchVideo v, bool flat = false)
    {
        string Key(string name) => flat ? prefix + char.ToUpperInvariant(name[0]) + name[1..] : prefix + name;
        c.Args[Key("id")] = v.Id;
        c.Args[Key("title")] = v.Title;
        c.Args[Key("url")] = v.Url;
        c.Args[Key("type")] = v.Type;
        c.Args[Key("duration")] = v.Duration;
        c.Args[Key("views")] = v.ViewCount;
        c.Args[Key("createdAt")] = Iso(v.CreatedAt);
        c.Args[Key("thumbnail")] = v.ThumbnailUrl;
    }

    private static void WriteAutomod(ActionRunContext c, TwitchAutomodSettings s)
    {
        c.Args["automodLevel"] = s.OverallLevel ?? -1;
        c.Args["automodDisability"] = s.Disability;
        c.Args["automodAggression"] = s.Aggression;
        c.Args["automodSexualitySexOrGender"] = s.SexualitySexOrGender;
        c.Args["automodMisogyny"] = s.Misogyny;
        c.Args["automodBullying"] = s.Bullying;
        c.Args["automodSwearing"] = s.Swearing;
        c.Args["automodRaceEthnicityOrReligion"] = s.RaceEthnicityOrReligion;
        c.Args["automodSexBasedTerms"] = s.SexBasedTerms;
    }
}
