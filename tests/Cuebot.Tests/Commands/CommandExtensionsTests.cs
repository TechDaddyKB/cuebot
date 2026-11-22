using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Commands;
using Cuebot.Core.Credits;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;

namespace Cuebot.Tests.Commands;

/// <summary>Reply-only commands, built-in permission groups, the denied trigger and the banned-account filter.</summary>
public class CommandExtensionsTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private readonly ModerationSettings _settings = new();
    private readonly AccountStandings _standings;
    private readonly FakeStatus _source = new();
    private readonly ViewerStatusCache _status;
    private readonly CommandService _service;
    private readonly EventRecorder _events;

    public CommandExtensionsTests()
    {
        _bus = new EventBus(_log);
        _store = new CommandStore(null, _log);
        _groups = new GroupStore(null, _log, _bus);
        _standings = new AccountStandings(_bus, _log, () => _settings, time: _clock);
        _status = new ViewerStatusCache([_source], _bus, _clock);
        _service = new CommandService(_store, _groups, _bus, _log, _clock, _status, _standings);
        _events = new EventRecorder(_bus, CommandService.TriggeredEvent, CommandService.CooldownEvent, CommandService.DeniedEvent);
    }

    private CommandDefinition Add(Action<CommandDefinition>? configure = null)
    {
        var command = new CommandDefinition { Name = "Hello", Commands = ["!hello"] };
        configure?.Invoke(command);
        _store.Save(command);
        return command;
    }

    private IReadOnlyList<BotEvent> Send(string text = "!hello", string user = "alice", string userId = "1", ChatRoles roles = ChatRoles.None,
        string platform = "Twitch")
    {
        _events.Clear();
        _bus.Publish(Chat.Message(text, user, userId, platform, roles));
        return _events.Events;
    }

    private async Task<BotEvent> SendAndWait(ChatRoles roles = ChatRoles.None)
    {
        _events.Clear();
        _bus.Publish(Chat.Message("!hello", "alice", "1", "Twitch", roles));
        for (var i = 0; i < 200 && _events.Events.Count == 0; i++) await Task.Delay(10);
        return Assert.Single(_events.Events);
    }

    private void BanEvent(string user, string userId, string platform = "Twitch") =>
        _bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["user"] = user, ["userId"] = userId,
        }));

    [Fact]
    public void A_command_saved_before_these_options_loads_with_the_same_behaviour()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<CommandDefinition>("""{"Name":"Old","Commands":["!old"],"MinimumRole":"Vip","AllowedGroups":["Regulars"]}""", Json.Options)!;
        Assert.Equal("", old.Reply);
        Assert.Equal("", old.Description);
        Assert.Empty(old.Arguments);
        Assert.Equal(CommandReplyAccount.Default, old.ReplyAs);
        _store.Save(old);

        Assert.Empty(Send("!old").Where(e => e.Type == CommandService.TriggeredEvent));
        Assert.Single(Send("!old", roles: ChatRoles.Vip));
    }

    [Fact]
    public void A_stored_group_named_like_a_built_in_one_keeps_its_meaning()
    {
        _groups.Add("@VIPs");
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = ["@VIPs"]; });
        Assert.Equal(CommandService.DeniedEvent, Assert.Single(Send(roles: ChatRoles.Vip)).Type);
    }

    [Theory]
    [InlineData("@Moderators", ChatRoles.Moderator)]
    [InlineData("@VIPs", ChatRoles.Vip)]
    [InlineData("@Subscribers", ChatRoles.Subscriber)]
    public void Role_groups_need_no_lookup(string group, ChatRoles roles)
    {
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = [group]; });
        Assert.Equal(CommandService.TriggeredEvent, Assert.Single(Send(roles: roles)).Type);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public async Task Followers_are_looked_up_once_and_remembered()
    {
        _source.Follower = true;
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = [BuiltInGroups.Followers]; });
        Assert.Equal(CommandService.TriggeredEvent, (await SendAndWait()).Type);
        Assert.Equal(CommandService.TriggeredEvent, Assert.Single(Send()).Type);
        Assert.Equal(1, _source.Calls);
    }

    [Fact]
    public async Task A_non_follower_is_denied_with_the_reason()
    {
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = [BuiltInGroups.Followers]; });
        var e = await SendAndWait();
        Assert.Equal(CommandService.DeniedEvent, e.Type);
        Assert.Equal("notFollower", e.Args["deniedReason"]);
        Assert.Equal("Broadcaster", e.Args["requiredRole"]);
    }

    [Fact]
    public async Task A_failed_lookup_never_opens_a_deny_rule()
    {
        _source.Fail = true;
        Add(c => c.DeniedGroups = [BuiltInGroups.Followers]);
        var e = await SendAndWait();
        Assert.Equal(CommandService.DeniedEvent, e.Type);
        Assert.Equal("statusUnknown", e.Args["deniedReason"]);
    }

    [Fact]
    public async Task Tier_groups_check_the_tier_and_skip_non_subscribers()
    {
        _source.Tier = 2;
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = [BuiltInGroups.SubscribersT2]; });
        Assert.Equal("tier", Assert.Single(Send()).Args["deniedReason"]);
        Assert.Equal(0, _source.Calls);
        _clock.Advance(6);
        Assert.Equal(CommandService.TriggeredEvent, (await SendAndWait(ChatRoles.Subscriber)).Type);
    }

    [Fact]
    public void Platforms_without_a_lookup_are_not_members()
    {
        Add(c => { c.MinimumRole = ChatPermission.Broadcaster; c.AllowedGroups = [BuiltInGroups.Followers]; });
        Assert.Equal("notFollower", Assert.Single(Send(platform: "Kick")).Args["deniedReason"]);
        Assert.Equal(0, _source.Calls);
    }

    [Fact]
    public void Denied_events_are_throttled_per_viewer()
    {
        Add(c => c.MinimumRole = ChatPermission.Moderator);
        Assert.Equal("role", Assert.Single(Send()).Args["deniedReason"]);
        Assert.Empty(Send());
        _clock.Advance(6);
        Assert.Single(Send());
    }

    [Fact]
    public void Preset_arguments_are_added_but_never_replace_chat_variables()
    {
        Add(c => c.Arguments = new() { ["sound"] = "airhorn-%input0%", ["user"] = "spoofed" });
        var e = Assert.Single(Send("!hello big"));
        Assert.Equal("airhorn-big", e.Args["sound"]);
        Assert.Equal("alice", e.Args["user"]);
    }

    [Fact]
    public async Task A_reply_is_sent_with_variables_on_the_same_platform()
    {
        var sender = new RecordingSender("Twitch");
        using var hub = new ChatHub(_bus, _log, [], [sender]);
        using var replier = new CommandReplier(_store, hub, _bus, _log);
        Add(c => c.Reply = "Hi %user%, you said %input0%");
        Send("!hello there");
        Assert.Equal("Hi alice, you said there", await sender.NextAsync());
    }

    [Fact]
    public async Task Rotating_replies_take_turns()
    {
        var sender = new RecordingSender("Twitch");
        using var hub = new ChatHub(_bus, _log, [], [sender]);
        using var replier = new CommandReplier(_store, hub, _bus, _log);
        Add(c => { c.Reply = "one\ntwo"; c.ReplyMode = CommandReplyMode.Rotate; });
        Send();
        Assert.Equal("one", await sender.NextAsync());
        Send();
        Assert.Equal("two", await sender.NextAsync());
        Send();
        Assert.Equal("one", await sender.NextAsync());
    }

    [Fact]
    public void A_reply_command_ignores_internal_messages_even_when_told_not_to()
    {
        Add(c => { c.Reply = "hi"; c.IgnoreInternal = false; c.IgnoreBotAccount = false; });
        _events.Clear();
        _bus.Publish(Chat.Message("!hello", "bot", "9", "Twitch", ChatRoles.None, new() { ["isInternal"] = true }));
        Assert.Empty(_events.Events);
    }

    [Theory]
    [InlineData("%input%", "/ban streamer", true)]
    [InlineData("%input%", ".ban streamer", true)]
    [InlineData("/me waves", "/me waves", false)]
    [InlineData("%input%", "hello", false)]
    public void Viewer_text_cannot_turn_a_reply_into_a_chat_command(string template, string text, bool blocked) =>
        Assert.Equal(blocked, CommandReplier.BlockReason(template, text, []) is not null);

    [Fact]
    public void A_reply_cannot_start_another_reply_command()
    {
        var other = new CommandDefinition { Name = "Other", Commands = ["!other"], Reply = "!hello" };
        Assert.NotNull(CommandReplier.BlockReason("!other", "!other", [other]));
    }

    [Fact]
    public void Follows_from_banned_accounts_are_hidden_but_paid_and_test_events_are_not()
    {
        BanEvent("troll", "66");
        Assert.True(_standings.HidesActivity(FeedKind.Follow, false, "Twitch", "66", "troll"));
        Assert.False(_standings.HidesActivity(FeedKind.Follow, false, "Twitch", "66", "troll", isTest: true));
        Assert.False(_standings.HidesActivity(FeedKind.Tip, true, "Twitch", "66", "troll"));
        Assert.False(_standings.HidesActivity(FeedKind.Redemption, false, "Twitch", "66", "troll"));
        Assert.False(_standings.HidesActivity(FeedKind.Follow, false, "Kick", "66", "troll"));

        _settings.IgnoreBannedFollows = false;
        Assert.False(_standings.HidesActivity(FeedKind.Follow, false, "Twitch", "66", "troll"));
    }

    [Fact]
    public void Known_bots_are_only_filtered_when_asked()
    {
        Assert.True(_standings.IsKnownBot("NightBot"));
        Assert.False(_standings.HidesActivity(FeedKind.Follow, false, "Twitch", "5", "nightbot"));
        Assert.False(_standings.SilencesChatter("nightbot"));

        _settings.TreatKnownBotsAsBanned = true;
        _settings.ExtraBots = ["MyBot"];
        Assert.True(_standings.HidesActivity(FeedKind.Follow, false, "Twitch", "5", "nightbot"));
        Assert.True(_standings.SilencesChatter("mybot"));
    }

    [Fact]
    public void Chatting_after_a_ban_proves_it_was_lifted_but_a_message_in_transit_does_not()
    {
        BanEvent("troll", "66");
        _clock.Advance(5);
        Send("hi", "troll", "66");
        Assert.True(_standings.IsBanned("Twitch", "66", "troll"));

        _clock.Advance(300);
        Send("hi", "troll", "66");
        Assert.False(_standings.IsBanned("Twitch", "66", "troll"));
    }

    [Fact]
    public void An_unban_clears_the_account()
    {
        BanEvent("troll", "66");
        _bus.Publish(new BotEvent(ChatHub.ModerationActionEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["action"] = "unban", ["user"] = "troll", ["userId"] = "66",
        }));
        Assert.False(_standings.IsBanned("Twitch", "66", "troll"));
    }

    [Fact]
    public async Task The_ban_list_counts_and_replaces_older_chat_proof()
    {
        var source = new FakeBans();
        using var standings = new AccountStandings(_bus, _log, () => _settings, sources: [source], time: _clock);
        Send("hi", "old", "77");
        _clock.Advance(300);
        await standings.SyncAsync();
        Assert.True(standings.IsBanned("Twitch", "77", "old"));
    }

    [Fact]
    public void A_banned_viewer_leaves_the_credits_and_stays_out()
    {
        using var credits = new CreditsCollector(null, _bus, _log, _clock, _standings);
        Send("hi", "troll", "66");
        Assert.Contains(credits.Snapshot().Sections.SelectMany(s => s.Entries), x => x.User == "troll");

        BanEvent("troll", "66");
        Assert.DoesNotContain(credits.Snapshot().Sections.SelectMany(s => s.Entries), x => x.User == "troll");

        _bus.Publish(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["kind"] = nameof(FeedKind.Follow), ["user"] = "troll", ["userId"] = "66",
        }));
        Assert.DoesNotContain(credits.Snapshot().Sections.SelectMany(s => s.Entries), x => x.User == "troll");
    }

    public void Dispose()
    {
        _service.Dispose();
        _status.Dispose();
        _standings.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeStatus : IViewerStatusSource
    {
        private int _calls;
        public bool Follower { get; set; }
        public int Tier { get; set; }
        public bool Fail { get; set; }
        public int Calls => _calls;
        public string Platform => "Twitch";

        public Task<bool> IsFollowerAsync(string userId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Fail ? Task.FromException<bool>(new InvalidOperationException("no scope")) : Task.FromResult(Follower);
        }

        public Task<int> SubTierAsync(string userId, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Fail ? Task.FromException<int>(new InvalidOperationException("no scope")) : Task.FromResult(Tier);
        }
    }

    private sealed class FakeBans : IBanListSource
    {
        public string Platform => "Twitch";
        public bool CanRead => true;

        public Task<IReadOnlyList<BannedAccount>> GetBannedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BannedAccount>>([new BannedAccount("77", "old")]);
    }

    private sealed class RecordingSender(string platform) : IChatSender
    {
        private readonly System.Threading.Channels.Channel<string> _sent = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public string Platform => platform;
        public bool CanSend => true;

        public Task SendAsync(string text, CancellationToken ct = default)
        {
            _sent.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public async Task<string> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _sent.Reader.ReadAsync(timeout.Token);
        }
    }
}
