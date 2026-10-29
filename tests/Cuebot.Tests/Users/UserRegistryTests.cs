using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Users;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Users;

public class UserRegistryTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly UserRegistry _users;
    private readonly EventRecorder _events;

    public UserRegistryTests()
    {
        _bus = new EventBus(_log);
        _users = new UserRegistry(null, _bus, _log, _clock);
        _events = new EventRecorder(_bus, UserEvents.FirstWords, UserEvents.PresentViewers);
    }

    private void Say(string user, string userId, string platform = "Twitch", ChatRoles roles = ChatRoles.None, string text = "hi") =>
        _bus.Publish(Chat.Message(text, user, userId, platform, roles));

    private static BotEvent StreamOnline(string platform) =>
        new(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["platform"] = platform, ["kind"] = "StreamOnline" });

    [Fact]
    public void Tracks_users_with_first_and_last_seen_message_count_and_roles()
    {
        var seen = new List<UserRecord>();
        _users.UserSeen += seen.Add;
        Say("Alice", "1", roles: ChatRoles.Subscriber);
        var first = _clock.Now;
        _clock.Advance(60);
        Say("AliceRenamed", "1", roles: ChatRoles.Subscriber | ChatRoles.Vip);
        Say("Bob", "2", platform: "Rumble");

        Assert.Equal(2, _users.Count);
        var alice = _users.Get("twitch", "1")!;
        Assert.Equal("AliceRenamed", alice.DisplayName);
        Assert.Equal(first, alice.FirstSeen);
        Assert.Equal(first.AddSeconds(60), alice.LastSeen);
        Assert.Equal(2, alice.MessageCount);
        Assert.Equal(ChatRoles.Subscriber | ChatRoles.Vip, alice.Roles);
        Assert.Equal("2", _users.FindByName("Rumble", "@bob")!.UserId);
        Assert.Null(_users.FindByName("Twitch", "bob"));
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void First_words_carry_the_platforms_chat_variables_like_streamerbot()
    {
        var platform = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["broadcast.title"] = "Live", ["publishedAt"] = new DateTime(2026, 1, 1), ["message"] = "spoofed",
        };
        _bus.Publish(Chat.Message("hi", "Ann", "9", "YouTube", extra: new() { [ChatHub.PlatformArgsKey] = platform }));

        var e = Assert.Single(_events.OfType(UserEvents.FirstWords));
        Assert.Equal(("Live", new DateTime(2026, 1, 1), "hi"), (e.Args["broadcast.title"], e.Args["publishedAt"], e.Args["message"]));
        Assert.False(e.Args.ContainsKey(ChatHub.PlatformArgsKey));
    }

    [Fact]
    public void First_words_fire_once_per_user_until_reset()
    {
        Say("Alice", "1");
        Say("Alice", "1");
        Say("Bob", "2");
        Say("Alice", "1", platform: "Rumble");
        var first = _events.OfType(UserEvents.FirstWords);
        Assert.Equal(["Alice", "Bob", "Alice"], first.Select(e => (string)e.Args["user"]!));
        Assert.Equal(true, first[0].Args["isNewUser"]);
        Assert.Equal(true, first[0].Args["firstMessage"]);
        Assert.Equal("hi", first[0].Args["message"]);
        Assert.True(_users.HasSpoken("Twitch", "1"));

        Assert.Equal(2, _users.ResetFirstWords("twitch"));
        Assert.False(_users.HasSpoken("Twitch", "1"));
        Assert.True(_users.HasSpoken("Rumble", "1"));
        _events.Clear();
        Say("Alice", "1");
        Say("Alice", "1", platform: "Rumble");
        var again = Assert.Single(_events.OfType(UserEvents.FirstWords));
        Assert.Equal(false, again.Args["isNewUser"]);
        Assert.Equal(3L, again.Args["messageCount"]);

        Assert.Equal(2, _users.ResetFirstWords());
    }

    [Fact]
    public void Stream_going_live_resets_first_words_for_that_platform_unless_disabled()
    {
        Say("Alice", "1");
        Say("Bob", "2", platform: "Rumble");
        _bus.Publish(StreamOnline("Twitch"));
        Assert.False(_users.HasSpoken("Twitch", "1"));
        Assert.True(_users.HasSpoken("Rumble", "2"));

        _users.UpdateSettings(s => s.ResetFirstWordsOnStreamOnline = false);
        Say("Alice", "1");
        _bus.Publish(StreamOnline("Twitch"));
        Assert.True(_users.HasSpoken("Twitch", "1"));
    }

    [Fact]
    public void Present_users_are_those_active_within_the_window()
    {
        Say("Old", "1");
        _clock.Advance(9 * 60);
        Say("Recent", "2");
        Say("Other", "3", platform: "Rumble");
        Assert.Equal(3, _users.PresentUsers().Count);
        Assert.Equal("Old", _users.PresentUsers().Last().DisplayName); // newest first

        _clock.Advance(2 * 60); // Old is now 11 minutes ago
        Assert.Equal(["Recent"], _users.PresentUsers("Twitch").Select(u => u.DisplayName));
        Assert.Equal(2, _users.PresentUsers("All").Count);
        Assert.Empty(_users.PresentUsers(window: TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Random_present_user_can_exclude_the_caller()
    {
        Say("Alice", "1");
        Say("Bob", "2");
        for (var i = 0; i < 20; i++)
            Assert.Equal("Bob", _users.RandomPresentUser("Twitch", "twitch:1", new Random(i))!.DisplayName);
        Assert.Null(_users.RandomPresentUser("Rumble"));
    }

    [Fact]
    public void Present_viewers_event_fires_every_interval_per_platform()
    {
        _users.UpdateSettings(s =>
        {
            s.PresentIntervalMinutes = 5;
            s.PresentMinutes = 10;
        });
        _users.Tick(_clock.Now); // arms
        Say("Alice", "1");
        Say("Bob", "2");
        Say("Carol", "3", platform: "Rumble");
        _clock.Advance(4 * 60);
        _users.Tick(_clock.Now);
        Assert.Empty(_events.OfType(UserEvents.PresentViewers));

        _clock.Advance(60);
        _users.Tick(_clock.Now);
        var events = _events.OfType(UserEvents.PresentViewers).OrderBy(e => (string)e.Args["platform"]!).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("Rumble", events[0].Args["platform"]);
        Assert.Equal(1, events[0].Args["count"]);
        Assert.Equal(2, events[1].Args["count"]);
        Assert.Contains("Alice", (string)events[1].Args["userNames"]!);
        // Streamer.bot's shape: a list of user dictionaries.
        var users = Assert.IsType<List<Dictionary<string, object?>>>(events[1].Args["users"]);
        Assert.Contains(users, u => (string?)u["display"] == "Alice" && u["role"] is 1 && u["isSubscribed"] is false);
        Assert.Equal(false, events[1].Args["isLive"]);

        _events.Clear();
        _clock.Advance(11 * 60); // everybody went quiet
        _users.Tick(_clock.Now);
        Assert.Empty(_events.OfType(UserEvents.PresentViewers));
    }

    [Fact]
    public void Present_viewers_get_the_platforms_stream_details()
    {
        using var users = new UserRegistry(null, _bus, _log, _clock, () => [new FakeStreamDetails("rumble"), new FailingStreamDetails()]);
        users.UpdateSettings(s => s.PresentIntervalMinutes = 5);
        users.Tick(_clock.Now);
        Say("Alice", "1");
        Say("Carol", "3", platform: "Rumble");
        _events.Clear();
        _clock.Advance(5 * 60);
        users.Tick(_clock.Now);

        var events = _events.OfType(UserEvents.PresentViewers).ToArray();
        var rumble = Assert.Single(events, e => (string)e.Args["platform"]! == "Rumble");
        Assert.Equal(true, rumble.Args["isLive"]);
        Assert.Equal("Live now", rumble.Args["title"]);
        var twitch = Assert.Single(events, e => (string)e.Args["platform"]! == "Twitch");
        Assert.Equal(false, twitch.Args["isLive"]);
        Assert.False(twitch.Args.ContainsKey("title"));
    }

    private sealed class FakeStreamDetails(string platform) : IPresentViewersArgs
    {
        public string Platform => platform;

        public void Add(IDictionary<string, object?> args)
        {
            args["isLive"] = true;
            args["title"] = "Live now";
        }
    }

    private sealed class FailingStreamDetails : IPresentViewersArgs
    {
        public string Platform => "twitch";

        public void Add(IDictionary<string, object?> args) => throw new InvalidOperationException("offline");
    }

    [Fact]
    public void Internal_and_anonymous_messages_are_not_tracked()
    {
        _bus.Publish(Chat.Message("hi", "Me", "0", extra: new() { ["isInternal"] = true }));
        _bus.Publish(Chat.Message("hi", "", "", "Twitch"));
        Assert.Equal(0, _users.Count);
    }

    [Fact]
    public void Users_without_ids_are_keyed_by_name()
    {
        Say("NoId", "");
        Say("noid", "");
        Assert.Equal(1, _users.Count);
        Assert.Equal(2, _users.All().Single().MessageCount);
    }

    public void Dispose()
    {
        _users.Dispose();
        _log.Dispose();
    }
}

public class UserPersistenceTests
{
    [Fact]
    public void Users_and_settings_survive_restart_but_first_words_do_not()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        var bus = new EventBus(log);
        var clock = new TestClock();
        using (var users = new UserRegistry(paths, bus, log, clock))
        {
            users.UpdateSettings(s => s.PresentMinutes = 3);
            bus.Publish(Chat.Message("hi", "Alice", "1"));
        }

        var bus2 = new EventBus(log);
        var events = new EventRecorder(bus2, UserEvents.FirstWords);
        using var reloaded = new UserRegistry(paths, bus2, log, clock);
        Assert.Equal(3, reloaded.Settings.PresentMinutes);
        Assert.Equal(1, reloaded.Get("Twitch", "1")!.MessageCount);
        Assert.False(reloaded.HasSpoken("Twitch", "1"));
        bus2.Publish(Chat.Message("hi again", "Alice", "1"));
        Assert.Single(events.Events);
        Assert.Equal(2, reloaded.Get("Twitch", "1")!.MessageCount);
    }

    [Fact]
    public void Changes_are_saved_in_batches_on_tick()
    {
        using var dir = new TempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        var bus = new EventBus(log);
        var clock = new TestClock();
        using var users = new UserRegistry(paths, bus, log, clock);
        bus.Publish(Chat.Message("hi", "Alice", "1"));
        ((IPeriodicService)users).Tick(clock.Now);
        Assert.False(File.Exists(paths.File("users.json"))); // not yet: saves are batched

        clock.Advance(20);
        users.Tick(clock.Now);
        users.Flush();
        Assert.Contains("Alice", File.ReadAllText(paths.File("users.json")));
    }
}
