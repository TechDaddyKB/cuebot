using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Polls;
using Cuebot.Core.Variables;
using Cuebot.Tests.ChatExtras;

namespace Cuebot.Tests.Polls;

public sealed class ChatPollsTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly ManualClock _clock = new();
    private readonly PointsService _points;
    private readonly TempDir _dir = new();
    private readonly List<OverlayMessage> _sent = [];
    private readonly List<BotEvent> _events = [];

    public ChatPollsTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(_sent.Add);
        _bus.Subscribe(e => { if (e.Type.StartsWith("cuebot.", StringComparison.Ordinal)) _events.Add(e); });
        _points = new PointsService(new UserVariables(null, _log, _clock), null, _bus, _log, time: _clock);
    }

    public void Dispose()
    {
        _points.Dispose();
        _dir.Dispose();
    }

    private (PollTracker Tracker, ChatPolls Polls) Create(AppPaths? paths = null)
    {
        var tracker = new PollTracker(_bus, _feed, [], _log);
        return (tracker, new ChatPolls(paths, _log, _bus, tracker, _points, _clock));
    }

    private void Chat(string platform, string user, string text) =>
        _bus.Publish(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["user"] = user, ["userId"] = user.ToLowerInvariant() + "-id", ["message"] = text,
        }));

    private JsonObject LastPoll() =>
        JsonNode.Parse(_sent.Last(m => m.Json.Contains("cuebot-poll", StringComparison.Ordinal)).Json)!["event"]!.AsObject();

    private static long[] Votes(PollState state) => state.Choices.Select(c => c.Votes).ToArray();

    [Fact]
    public void Viewers_on_every_platform_vote_once_by_number_or_title()
    {
        var (tracker, polls) = Create();
        using (tracker)
        using (polls)
        {
            polls.StartPoll("Next game?", ["Horror", "Racing", "Just chatting"], 60);
            Assert.Throws<InvalidOperationException>(() => polls.StartPoll("Another", ["A", "B"], 60));

            Chat("Twitch", "Ann", "!vote 1");
            Chat("Kick", "Bob", "!VOTE racing");
            Chat("Rumble", "Cy", "!vote 3");
            Chat("YouTube", "Dee", "!vote 2");
            Chat("Twitch", "Ann", "!vote 2");      // changes her vote
            Chat("Kick", "Eve", "!vote 9");        // no such choice
            Chat("Kick", "Fay", "2");              // bare numbers are off by default
            Assert.Equal([0L, 3, 1], Votes(tracker.Current.Single()));
            Assert.True(_sent.Last().Droppable);   // vote counts may be dropped for a slow page

            var ended = polls.EndPoll()!;
            Assert.Equal((PollState.Ended, 1, "Racing"), (ended.Status, ended.Winner, ended.Choices[ended.Winner].Title));
            Assert.False(_sent.Last().Droppable);  // the result never is
            Assert.Equal(("ended", 1, "!vote"), (LastPoll()["status"]!.GetValue<string>(), LastPoll()["winner"]!.GetValue<int>(),
                LastPoll()["command"]!.GetValue<string>()));
            var end = _events.Single(e => e.Type == PollEvents.PollEnded);
            Assert.Equal(("Racing", 3L, 4L), (end.Args["winningChoice"], end.Args["winningVotes"], end.Args["totalVotes"]));
            Assert.Null(polls.EndPoll());
        }
    }

    [Fact]
    public void A_poll_ends_when_its_time_is_up_and_numbers_can_vote()
    {
        var (tracker, polls) = Create();
        using (tracker)
        using (polls)
        {
            polls.SetCommands("!pick", null, numbersVote: true);
            polls.StartPoll("Snack?", ["Chips", "Fruit"], 30);
            Chat("Twitch", "Ann", "2");
            Chat("Kick", "Bob", "!pick chips");
            Chat("Kick", "Cy", "!vote 1");          // the old command no longer counts
            _clock.Advance(TimeSpan.FromSeconds(29));
            polls.Tick(_clock.GetUtcNow());
            Assert.True(polls.PollRunning);
            _clock.Advance(TimeSpan.FromSeconds(2));
            polls.Tick(_clock.GetUtcNow());
            Assert.False(polls.PollRunning);
            Assert.Equal([1L, 1], Votes(tracker.Current.Single()));
            Assert.Throws<ArgumentException>(() => polls.StartPoll("One choice", ["Only"], 30));
        }
    }

    [Fact]
    public void Predictions_take_points_and_pay_winners_from_the_whole_pot()
    {
        _points.Add("Twitch", "ann-id", "Ann", 1000);
        _points.Add("Kick", "bob-id", "Bob", 1000);
        _points.Add("Rumble", "cy-id", "Cy", 1000);
        var (tracker, polls) = Create();
        using (tracker)
        using (polls)
        {
            polls.StartPrediction("Will we win?", ["Yes", "No"], 60);
            Chat("Twitch", "Ann", "!bet 1 100");
            Chat("Kick", "Bob", "!bet no 300");
            Chat("Rumble", "Cy", "!bet yes 50");
            Chat("Rumble", "Cy", "!bet 1 50");        // adds to the same side
            Chat("Rumble", "Cy", "!bet 2 50");        // the other side is refused
            Chat("Twitch", "Ann", "!bet 1 5000");     // more than she has
            Assert.Equal((900L, 700L, 900L), (_points.Get("Twitch", "ann-id"), _points.Get("Kick", "bob-id"), _points.Get("Rumble", "cy-id")));
            var open = tracker.Current.Single();
            Assert.Equal([2L, 1], Votes(open));
            Assert.Equal([200L, 300], open.Choices.Select(c => c.Points));

            polls.LockPrediction();
            Chat("Kick", "Bob", "!bet 2 100");        // locked: no more bets
            Assert.Equal(700, _points.Get("Kick", "bob-id"));

            var resolved = polls.ResolvePrediction(0)!;
            // Pot 500, winning side 200: each winner gets 2.5 times their bet.
            Assert.Equal((1150L, 700L, 1150L), (_points.Get("Twitch", "ann-id"), _points.Get("Kick", "bob-id"), _points.Get("Rumble", "cy-id")));
            Assert.Equal((PollState.Ended, 0), (resolved.Status, resolved.Winner));
            var paid = _events.Single(e => e.Type == PollEvents.PredictionResolved);
            Assert.Equal(("Yes", 2, 500L), (paid.Args["winningOutcome"], paid.Args["winnerCount"], paid.Args["paidOut"]));
            Assert.Null(polls.CancelPrediction());
        }
    }

    [Fact]
    public void Cancelling_refunds_and_bets_survive_a_restart()
    {
        _points.Add("Twitch", "ann-id", "Ann", 500);
        var paths = new AppPaths(_dir.Path);
        var (tracker, polls) = Create(paths);
        polls.StartPrediction("Boss down first try?", ["Yes", "No"], 0);
        Chat("Twitch", "Ann", "!bet 2 all");
        Assert.Equal(0, _points.Get("Twitch", "ann-id"));
        polls.Dispose();
        tracker.Dispose();

        var (again, restarted) = Create(paths);
        using (again)
        using (restarted)
        {
            Assert.Equal([0L, 1], Votes(again.Current.Single()));   // the open prediction is back, with its bet
            restarted.CancelPrediction();
            Assert.Equal(500, _points.Get("Twitch", "ann-id"));
            Assert.Equal(PollState.Canceled, again.Current.Single().Status);
        }
    }

    [Fact]
    public void Platform_polls_come_in_through_their_adapters()
    {
        var adapter = new FakeAdapter();
        using var tracker = new PollTracker(_bus, _feed, [adapter], _log);
        _bus.Publish(new BotEvent("fake.poll", new Dictionary<string, object?>()));
        _bus.Publish(new BotEvent("fake.other", new Dictionary<string, object?>()));
        var state = Assert.Single(tracker.Current);
        Assert.Equal(("fake", 50.0), (state.Source, LastPoll()["choices"]![0]!["percent"]!.GetValue<double>()));
        Assert.Equal("fake", tracker.Snapshot()[0]!["source"]!.GetValue<string>());
    }

    private sealed class FakeAdapter : IPollAdapter
    {
        public PollState? ToPoll(BotEvent e) => e.Type == "fake.poll"
            ? new PollState("fake", PollState.Poll, "p1", "Q", PollState.Active, [new("a", "A", 1), new("b", "B", 1)])
            : null;
    }
}
