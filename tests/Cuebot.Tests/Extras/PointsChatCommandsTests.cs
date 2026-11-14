using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class PointsChatCommandsTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly UserVariables _vars;
    private readonly AccountLinks _links;
    private readonly PointsService _points;
    private readonly CommandStore _store;
    private readonly FixedRandom _random = new();
    private readonly PointsChatCommands _commands;
    private readonly List<(string Platform, string Text)> _said = [];

    public PointsChatCommandsTests()
    {
        _bus = new EventBus(_log);
        _vars = new UserVariables(null, _log, _clock);
        _links = new AccountLinks(null, _log, _clock);
        _points = new PointsService(_vars, null, _bus, _log, time: _clock, links: _links);
        _points.UpdateSettings(s => { s.MessageAmount = 0; s.OnlyWhileLive = true; });
        _store = new CommandStore(null, _log);
        _commands = new PointsChatCommands(_points, _bus, _log, (p, t) => { _said.Add((p, t)); return Task.CompletedTask; },
            links: _links, commands: _store, time: _clock, random: _random);
        // No cooldowns in tests unless a test sets them.
        _commands.UpdateSettings(s => s.Commands.ForEach(c => { c.CooldownSeconds = 0; c.UserCooldownSeconds = 0; }));
    }

    private string Say(string text, string user = "alice", string id = "1", string platform = "Twitch")
    {
        _said.Clear();
        _bus.Publish(Chat.Message(text, user, id, platform));
        return string.Join(" | ", _said.Select(s => s.Text));
    }

    private void LinkAliceTwitchToKick()
    {
        Say("!linkkick alice_k", "alice", "1", "Twitch");
        Say("!linktwitch alice", "alice_k", "k1", "Kick");
    }

    [Fact]
    public void Points_answers_for_yourself_and_for_others()
    {
        _points.Add("Twitch", "1", "alice", 1250);
        _points.Add("Twitch", "2", "bob", 300);
        Assert.Equal("alice has 1,250 points (rank #1)", Say("!points"));
        Assert.Equal("bob has 300 points (rank #2)", Say("!points @bob"));
        Assert.Equal("alice, I don't know nobody yet.", Say("!points nobody"));
    }

    [Fact]
    public void Linking_needs_both_sides_and_then_shares_one_wallet()
    {
        _points.Add("Twitch", "1", "alice", 1000);
        _points.Add("Kick", "k1", "alice_k", 250);

        var asked = Say("!linkkick alice_k", "alice", "1", "Twitch");
        Assert.Contains("type !linktwitch alice", asked);
        Assert.False(_links.IsLinked("Twitch", "1"));
        Assert.Equal(1000, _points.Get("Twitch", "1"));

        var linked = Say("!linktwitch alice", "alice_k", "k1", "Kick");
        Assert.StartsWith("Linked! alice (Twitch) + alice_k (Kick) now share 1,250 points.", linked);
        Assert.Contains(_said, s => s.Platform == "Twitch");   // the first chat hears it too
        Assert.Equal(1250, _points.Get("Twitch", "1"));
        Assert.Equal(1250, _points.Get("Kick", "k1"));
        Assert.Equal("alice has 1,250 points (rank #1) · Twitch 1,000 · Kick 250", Say("!points"));

        // Spending from Kick takes Kick's own points first, then Twitch's.
        Assert.True(_points.TrySpend("Kick", "k1", "alice_k", 400, out var left));
        Assert.Equal(850, left);
        Assert.Equal(0, _points.Shares("Kick", "k1").Single(s => s.Platform == "kick").Points);
        Assert.Equal(850, _points.Shares("Kick", "k1").Single(s => s.Platform == "twitch").Points);

        // Earnings stay on the platform that earned them.
        _points.Add("Kick", "k1", "alice_k", 50);
        Assert.Equal(50, _points.Shares("Kick", "k1").Single(s => s.Platform == "kick").Points);

        Assert.Contains("unlinked", Say("!unlink kick"));
        Assert.Equal(850, _points.Get("Twitch", "1"));
        Assert.Equal(50, _points.Get("Kick", "k1"));
    }

    [Fact]
    public void A_link_request_names_the_wrong_account_or_expires()
    {
        Say("!linkkick alice_k", "alice", "1", "Twitch");
        Say("!linktwitch somebody", "alice_k", "k1", "Kick");   // does not match alice's request
        Assert.False(_links.IsLinked("Kick", "k1"));

        _clock.Advance(11 * 60);
        Say("!linktwitch alice", "alice_k", "k1", "Kick");      // alice's request ran out
        Assert.False(_links.IsLinked("Kick", "k1"));
        Assert.Contains("you're already on", Say("!linktwitch x", "alice_k", "k1", "Twitch"));
    }

    [Fact]
    public void Leaderboards_combine_linked_accounts_or_show_one_platform()
    {
        _points.Add("Twitch", "1", "alice", 500);
        _points.Add("Kick", "k1", "alice_k", 400);
        _points.Add("Twitch", "2", "bob", 800);
        LinkAliceTwitchToKick();

        Assert.Equal("Top points: 1. alice 900 · 2. bob 800", Say("!top"));
        Assert.Equal("Top Twitch points: 1. bob 800 · 2. alice 500", Say("!toptwitch"));
        Assert.Equal("Top Kick points: 1. alice_k 400", Say("!topkick"));
        Assert.Equal("Nobody has any points yet.", Say("!toprumble"));
    }

    [Fact]
    public void Give_moves_points_and_refuses_your_own_linked_account()
    {
        _points.Add("Twitch", "1", "alice", 100);
        _points.Add("Twitch", "2", "bob", 0);
        Assert.Equal("alice gave 40 points to bob. alice now has 60 points.", Say("!give @bob 40"));
        Assert.Equal(40, _points.Get("Twitch", "2"));
        Assert.Equal("alice, you only have 60 points.", Say("!give bob 100"));
        Assert.Equal("alice, use !give name amount.", Say("!give bob"));
    }

    [Fact]
    public void Gamble_doubles_on_a_high_roll_and_loses_on_a_low_one()
    {
        _points.Add("Twitch", "1", "alice", 100);
        _random.Rolls.Enqueue(90);   // above 55: win (45%)
        Assert.Equal("alice rolled 90 and won 50 points! Now 150 points.", Say("!gamble 50"));
        _random.Rolls.Enqueue(99);   // above 98: jackpot (2%)
        Assert.Equal("alice rolled 99, JACKPOT! Won 100 points. Now 250 points.", Say("!gamble 50"));
        _random.Rolls.Enqueue(10);
        Assert.Equal("alice rolled 10 and lost 125 points. Now 125 points.", Say("!gamble 50%"));
        Assert.Equal("alice, bet from 10 points to any amount.", Say("!gamble 5"));
        Assert.Equal("alice, you only have 125 points.", Say("!gamble 500"));
    }

    [Fact]
    public void Slots_pay_for_three_and_two_of_a_kind()
    {
        _points.UpdateSettings(s => { });
        _commands.UpdateSettings(s => s.SlotSymbols = "A B C");
        _points.Add("Twitch", "1", "alice", 100);
        foreach (var n in new[] { 0, 0, 0 }) _random.Rolls.Enqueue(n);
        Assert.Equal("[ A A A ] alice hit three of a kind and won 90 points! Now 190 points.", Say("!slots 10"));
        foreach (var n in new[] { 0, 1, 0 }) _random.Rolls.Enqueue(n);
        Assert.Equal("[ A B A ] alice got a pair and won 5 points. Now 195 points.", Say("!slots 10"));
        foreach (var n in new[] { 0, 1, 2 }) _random.Rolls.Enqueue(n);
        Assert.Equal("[ A B C ] alice lost 10 points. Now 185 points.", Say("!slots 10"));
    }

    [Fact]
    public void A_duel_waits_for_accept_and_the_winner_takes_both_stakes()
    {
        _points.Add("Twitch", "1", "alice", 100);
        _points.Add("Twitch", "2", "bob", 100);
        Assert.Equal("bob, alice challenges you to a duel for 50 points! Type !accept within 60 seconds.", Say("!duel bob 50"));
        Assert.Equal("", Say("!accept", "carol", "3"));   // not for carol
        _random.Rolls.Enqueue(1);                          // the one who accepted wins
        Assert.Equal("bob beat alice in a duel and won 50 points!", Say("!accept", "bob", "2"));
        Assert.Equal((50L, 150L), (_points.Get("Twitch", "1"), _points.Get("Twitch", "2")));

        Say("!duel bob 10");
        _clock.Advance(61);
        Assert.Equal("", Say("!accept", "bob", "2"));      // too late
    }

    [Fact]
    public void A_command_the_streamer_made_with_the_same_word_wins()
    {
        _points.Add("Twitch", "1", "alice", 10);
        _store.Save(new CommandDefinition { Name = "My points", Commands = ["!points"] });
        Assert.Equal("", Say("!points"));
        _commands.UpdateCommand(PointsCommandKind.Leaderboard, c => c.Enabled = false);
        Assert.Equal("", Say("!top"));
        Assert.StartsWith("Top Twitch", Say("!toptwitch"));
    }

    [Fact]
    public void A_twitch_command_trigger_with_the_same_word_wins_in_twitch_chat()
    {
        using var profile = new Cuebot.Core.Actions.ProfileService(null, _log);
        profile.Profile.Actions.Add(new Cuebot.Core.Actions.ActionDefinition
        {
            Name = "My leaderboard",
            Triggers = [new Cuebot.Core.Actions.TriggerDefinition { Type = "twitch.command", Settings = { ["command"] = "!top" } }],
        });
        profile.NotifyChanged();
        using var commands = new PointsChatCommands(_points, _bus, _log, (p, t) => { _said.Add((p, t)); return Task.CompletedTask; },
            links: _links, commands: _store, time: _clock, random: _random, profile: profile);
        _commands.UpdateCommand(PointsCommandKind.Leaderboard, c => c.Enabled = false);   // only the new service answers
        commands.UpdateSettings(s => s.Commands.ForEach(c => c.CooldownSeconds = 0));
        _points.Add("Kick", "k1", "alice_k", 5);
        Assert.Equal("", Say("!top", "alice", "1", "Twitch"));
        Assert.StartsWith("Top points", Say("!top", "alice_k", "k1", "Kick"));
    }

    [Fact]
    public void Cooldowns_and_roles_are_respected()
    {
        _points.Add("Twitch", "1", "alice", 10);
        _commands.UpdateCommand(PointsCommandKind.Points, c => c.UserCooldownSeconds = 30);
        Assert.NotEqual("", Say("!points"));
        Assert.Equal("", Say("!points"));
        _clock.Advance(31);
        Assert.NotEqual("", Say("!points"));
        _commands.UpdateCommand(PointsCommandKind.Give, c => c.MinimumRole = ChatPermission.Moderator);
        Assert.Equal("", Say("!give bob 1"));
    }

    [Fact]
    public void Support_names_each_kind_of_support_plainly()
    {
        var tally = new Cuebot.Core.Spending.SupportTally("USD", 52.3,
        [
            new("Super Chat", Cuebot.Core.Chat.SpendUnit.Money, 25.33, 25.33),
            new("Gift subs", Cuebot.Core.Chat.SpendUnit.GiftSubscription, 3, 14.97),
            new("Cheer", Cuebot.Core.Chat.SpendUnit.Bits, 1000, 10),
            new("Power-up", Cuebot.Core.Chat.SpendUnit.Bits, 200, 2),
        ]);
        Assert.Equal(["3 gifted subs", "1,200 bits", "$25.33 in Super Chat"], PointsChatCommands.SupportItems(tally));
    }

    [Theory]
    [InlineData("250", 1000, 250)]
    [InlineData("all", 1000, 1000)]
    [InlineData("50%", 1000, 500)]
    [InlineData("1k", 5000, 1000)]
    [InlineData("1,500", 5000, 1500)]
    [InlineData("nope", 1000, 0)]
    [InlineData("-5", 1000, 0)]
    public void Amounts_read_numbers_all_percent_and_thousands(string text, long balance, long expected) =>
        Assert.Equal(expected, PointsChatCommands.Amount(text, balance));

    [Fact]
    public void Settings_fill_in_commands_added_later()
    {
        var s = _commands.Settings;
        Assert.Equal(Enum.GetValues<PointsCommandKind>().Length, s.Commands.Count);
        Assert.False(s.Commands.Single(c => c.Kind == PointsCommandKind.TopSupporters).Enabled);   // money stays private until turned on
        Assert.True(s.Commands.Single(c => c.Kind == PointsCommandKind.Gamble).Enabled);
    }

    /// <summary>Next(min, max) and Next(max) return queued values (Next(1, 101) → the roll itself).</summary>
    private sealed class FixedRandom : Random
    {
        public Queue<int> Rolls { get; } = new();
        public override int Next(int minValue, int maxValue) => Rolls.Count > 0 ? Rolls.Dequeue() : minValue;
        public override int Next(int maxValue) => Rolls.Count > 0 ? Rolls.Dequeue() : 0;
    }

    public void Dispose()
    {
        _commands.Dispose();
        _points.Dispose();
    }
}
