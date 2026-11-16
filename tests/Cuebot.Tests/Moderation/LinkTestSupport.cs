using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;

namespace Cuebot.Tests.Moderation;

/// <summary>
/// Identity links and linked moderation wired to fakes: Twitch, YouTube (quota), Kick (whole minutes) and Rumble (by hand)
/// moderators, protected accounts, a moderator lookup, a user registry and a chat hub that records what is sent.
/// </summary>
internal sealed class LinkHarness : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory("cuebot-links-").FullName;
    public readonly ManualClock Time = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
    public readonly AppLog AppLog = new();
    public readonly EventBus Bus;
    public readonly ChatHub Hub;
    public readonly List<(string Platform, string Text)> Sent = [];
    public readonly ModLog Log;
    public readonly UserRegistry Users;
    public readonly ModerationSettings Settings = new() { IdentityLinksEnabled = true };
    public readonly LinkModerator Twitch = new("Twitch") { MaxReason = 500 };
    public readonly LinkModerator YouTube = new("YouTube") { QuotaCost = 200, PlatformRefPrefix = "YTBAN-" };
    public readonly LinkModerator Kick = new("Kick") { Unit = TimeSpan.FromMinutes(1), MaxReason = 100 };
    public readonly LinkModerator Rumble = new("Rumble") { Website = "https://rumble.com/chat/popup" };
    public readonly LinkQuota Quota = new();
    public readonly LinkPermissions Permissions = new();
    public readonly FakeLookup Lookup = new();
    public readonly IdentityLinks Links;
    public readonly ModerationService Service;
    public readonly List<ModerationStreamerNotice> Notices = [];

    public LinkHarness(bool withPermissions = true)
    {
        Bus = new EventBus(AppLog);
        Hub = new ChatHub(Bus, AppLog, [new HarnessAdapter("Twitch"), new HarnessAdapter("YouTube"), new HarnessAdapter("Kick")],
            [new RecordingSender("Twitch", Sent), new RecordingSender("YouTube", Sent), new RecordingSender("Kick", Sent)]);
        Log = new ModLog(Path.Combine(Dir, ModLog.FileName), Time);
        Users = new UserRegistry(null, Bus, AppLog, Time);
        Links = new IdentityLinks(Log, () => Settings, [new HarnessProtected()], [new HarnessRoles()], Users, Hub, [Lookup], AppLog, Time);
        Service = new ModerationService(new ChatModerationService([Twitch, YouTube, Kick, Rumble], []), Log, Hub, [new HarnessProtected()], [Quota],
            [new HarnessRoles()], () => Settings, AppLog, Time)
        {
            Links = Links,
        };
        if (withPermissions) Service.Permissions = Permissions;
        Service.StreamerNotice += Notices.Add;
    }

    public static readonly ModActor Sam = ModActor.Member("member-sam", "Sam", "device-1");
    public static readonly ModActor Alex = ModActor.Member("member-alex", "Alex", "device-2");

    public static ViewerRef Tw(string id = "tw1", string name = "rowdy_guy") => new("Twitch", id, name);
    public static ViewerRef Yt(string id = "UCyt1", string name = "Rowdy Guy") => new("YouTube", id, name);
    public static ViewerRef Kk(string id = "k1", string name = "rowdyguy") => new("Kick", id, name);
    public static ViewerRef Rb(string id = "r1", string name = "RowdyG") => new("Rumble", id, name);

    /// <summary>A viewer the registry has seen in chat.</summary>
    public void Seen(ViewerRef v, bool bot = false, bool moderator = false) =>
        Users.Handle(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = v.Platform, ["userId"] = v.UserId, ["user"] = v.UserName, ["userName"] = v.UserName.ToLowerInvariant(),
            ["message"] = "hi", ["isBotAccount"] = bot, ["isModerator"] = moderator,
        }));

    /// <summary>Links accounts as <paramref name="status"/> (suggested by Sam, confirmed by Alex, or verified with a code).</summary>
    public async Task<IdentityPerson> LinkAsync(ViewerRef a, ViewerRef b, LinkStatus status = LinkStatus.Confirmed)
    {
        if (status == LinkStatus.Verified)
        {
            var (code, _) = await Links.StartVerificationAsync(a, Sam);
            var verified = await Links.RedeemAsync(b, code!.Code);
            Assert.Equal(LinkVerifyOutcome.Verified, verified.Outcome);
            await Service.LastMirror;
            return verified.Person!;
        }
        var proposed = await Links.ProposeAsync(a, b, Sam, choice: LinkConflictChoice.Merge);
        Assert.True(proposed.Ok, proposed.Message);
        if (status == LinkStatus.Suggested) return proposed.Person!;
        var confirmed = Links.Confirm(proposed.LinkIds[0], Alex);
        Assert.True(confirmed.Ok, confirmed.Message);
        await Service.LastMirror;
        return confirmed.Person!;
    }

    public void Dispose()
    {
        Service.Dispose();
        Links.Dispose();
        Users.Dispose();
        Hub.Dispose();
        Log.Dispose();
        AppLog.Dispose();
        try { Directory.Delete(Dir, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>A moderator whose tools can be switched per action: works, on the website, not available; failing on demand.</summary>
internal sealed class LinkModerator(string platform) : IChatModerator
{
    public List<string> Calls { get; } = [];
    public int QuotaCost { get; init; }
    public int? MaxReason { get; set; }
    public TimeSpan Unit { get; set; } = TimeSpan.FromSeconds(1);
    public string? Website { get; set; }
    public string? PlatformRefPrefix { get; init; }
    public HashSet<ModerationAction> Failing { get; } = [];
    public HashSet<ModerationAction> Missing { get; } = [];
    private int _refs;
    public string Platform => platform;
    public ChatModerationActions Available => ChatModerationActions.Ban | ChatModerationActions.Timeout | ChatModerationActions.DeleteMessage;
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

    public ModerationCapability Capability(ModerationAction action)
    {
        if (Website is not null) return ModerationCapability.Website(action, Website, "Rumble has no moderation API.");
        if (Missing.Contains(action)) return ModerationCapability.NotAvailable(action, $"{platform} cannot do this now.");
        return ModerationCapability.Works(action) with { QuotaCost = QuotaCost, MaxReasonLength = MaxReason, DurationUnit = Unit };
    }

    public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
    {
        if (Failing.Contains(r.Action)) throw new InvalidOperationException($"{platform} said no");
        Calls.Add(r.Action switch
        {
            ModerationAction.Unban => $"Unban {r.UserId} ref={r.PlatformRef}",
            ModerationAction.Timeout => $"Timeout {r.UserId} {Capability(r.Action).Apply(r.Duration ?? TimeSpan.Zero).TotalSeconds}s {r.Reason}".TrimEnd(),
            _ => $"{r.Action} {r.UserId} {r.Reason}".TrimEnd(),
        });
        var applied = r.Action == ModerationAction.Timeout ? Capability(r.Action).Apply(r.Duration ?? TimeSpan.FromMinutes(10)) : (TimeSpan?)null;
        var reference = PlatformRefPrefix is not null && r.Action is ModerationAction.Ban or ModerationAction.Timeout ? PlatformRefPrefix + ++_refs : null;
        return Task.FromResult(ModerationOutcome.Done(applied, reference));
    }
}

internal sealed class LinkQuota : IPlatformQuota
{
    public int Left { get; set; } = 10_000;
    public string Platform => "YouTube";
    public int Remaining => Left;
    public int Reserve => 2_000;
    public DateTimeOffset ResetsAt => DateTimeOffset.UnixEpoch;
    public string? Refuse(int units) => Remaining - units < Reserve ? "Not enough YouTube quota left today" : null;
}

/// <summary>Like the portal: Sam is a standard mod, Alex may also confirm links.</summary>
internal sealed class LinkPermissions : IModerationPermissions
{
    public HashSet<string> Confirmers { get; } = ["member-alex"];
    public string? Refuse(ModActor actor, ModerationCommand command) => null;

    public string? RefuseLink(ModActor actor, LinkOperation operation) =>
        operation is LinkOperation.Confirm or LinkOperation.Reject or LinkOperation.Unlink or LinkOperation.DeleteNote
        && !Confirmers.Contains(actor.MemberId ?? "")
            ? "Your role does not include confirming links."
            : null;
}

internal sealed class HarnessProtected : IProtectedAccounts
{
    public string? WhyProtected(string platform, string userId, string userName) => userId switch
    {
        "bot-1" => "This is your bot account.",
        "portal-member" => "This is a member of your mod team.",
        _ => null,
    };
}

internal sealed class HarnessRoles : IChannelRoles
{
    public string Platform => "Twitch";
    public Task<bool> IsModeratorAsync(string userId, CancellationToken ct = default) => Task.FromResult(userId == "mod-by-lookup");
}

internal sealed class FakeLookup : IAccountLookup
{
    public Dictionary<string, string?> Names { get; } = [];
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public string Platform => "YouTube";

    public Task<string?> CurrentNameAsync(string userId, CancellationToken ct = default)
    {
        Calls++;
        if (Throw) throw new HttpRequestException("YouTube is down");
        return Task.FromResult(Names.GetValueOrDefault(userId));
    }
}

internal sealed class RecordingSender(string platform, List<(string Platform, string Text)> sent) : IChatSender
{
    public string Platform => platform;
    public bool CanSend => true;

    public Task SendAsync(string text, CancellationToken ct = default)
    {
        lock (sent) sent.Add((platform, text));
        return Task.CompletedTask;
    }
}

internal sealed class HarnessAdapter(string platform) : IChatEventAdapter
{
    public string Platform => platform;

    public ChatMessage? ToChatMessage(BotEvent e) => e.Type == $"{platform.ToLowerInvariant()}.chatMessage"
        ? new ChatMessage(platform, (string)e.Args["id"]!, (string)e.Args["user"]!, (string)e.Args["userId"]!, (string)e.Args["message"]!, e.Time)
        : null;

    public FeedItem? ToFeedItem(BotEvent e) => null;
}
