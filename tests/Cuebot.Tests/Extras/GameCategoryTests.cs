using Cuebot.Core.Events;
using Cuebot.Core.Games;
using Cuebot.Core.Logging;
using Cuebot.Core.Requests;
using Cuebot.Core.Storage;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class GameCategoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-games-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly StreamRequests _requests;
    private readonly JsonFile<CategorySettings> _file;
    private readonly FakeTarget _twitch = new("Twitch", ["Helldivers 2", "Just Chatting"]) { Current = "Just Chatting" };
    private readonly FakeTarget _kick = new("Kick", ["HELLDIVERS 2", "Just Chatting"]);
    private readonly FakeTarget _youtube = new("YouTube", []) { AnyIs = "Gaming" };
    private readonly CategorySwitcher _switcher;
    private readonly List<BotEvent> _events = [];

    public GameCategoryTests()
    {
        _bus = new EventBus(_log);
        _bus.Subscribe(_events.Add);
        _requests = new StreamRequests(_bus, _log, _clock);
        _file = new JsonFile<CategorySettings>(Path.Combine(_dir, "games.json"), _log);
        _switcher = new CategorySwitcher(_file, _requests, _bus, _log, () => [_twitch, _kick, _youtube]);
    }

    public void Dispose()
    {
        _switcher.Dispose();
        _file.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeTarget(string platform, string[] categories) : IStreamCategoryTarget
    {
        public string Platform => platform;
        public bool CanSet { get; set; } = true;
        public string? Current { get; set; }
        public string? AnyIs { get; init; }
        public List<string> Set { get; } = [];
        public bool Fail { get; set; }
        public Task<string?> CurrentAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task<string?> FindAsync(string game, CancellationToken ct = default) =>
            Task.FromResult(AnyIs ?? categories.FirstOrDefault(c => CategoryNames.Similar(c, game)));
        public Task<string> SetAsync(string category, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("Kick said no");
            Set.Add(category);
            Current = category;
            return Task.FromResult(category);
        }
    }

    private async Task Started(string game)
    {
        _bus.Publish(new BotEvent(GameEvents.Started, new Dictionary<string, object?> { ["game"] = game }));
        await _switcher.Last;
    }

    private async Task Stopped(string game)
    {
        _bus.Publish(new BotEvent(GameEvents.Stopped, new Dictionary<string, object?> { ["game"] = game }));
        await _switcher.Last;
    }

    [Fact]
    public async Task A_game_asks_once_and_nothing_changes_until_someone_says_yes()
    {
        await Started("Helldivers 2");
        var request = Assert.Single(_requests.Pending);
        Assert.Equal("Switch the stream category to Helldivers 2?", request.Title);
        Assert.Equal("Twitch: Helldivers 2 · Kick: HELLDIVERS 2", request.Detail);   // YouTube is off at first
        Assert.Equal("Always for this game", request.Remember);
        Assert.Empty(_twitch.Set);
        Assert.Contains(_events, e => e.Type == StreamRequests.AddedEvent);

        var outcome = await _requests.AnswerAsync(request.Id, RequestAnswer.Accept, "sam");
        Assert.Equal(RequestOutcome.Done, outcome.Result);
        Assert.Equal(["Helldivers 2"], _twitch.Set);
        Assert.Equal(["HELLDIVERS 2"], _kick.Set);
        Assert.Contains("Twitch (Helldivers 2), Kick (HELLDIVERS 2)", outcome.Message);
        Assert.Empty(_requests.Pending);
        Assert.Contains(_events, e => e.Type == StreamRequests.DecidedEvent && (string)e.Args["by"]! == "sam");
        Assert.Equal(RequestOutcome.NotPending, (await _requests.AnswerAsync(request.Id, RequestAnswer.Accept, "kris")).Result);
    }

    [Fact]
    public async Task No_request_without_a_matching_category_or_when_it_is_already_set()
    {
        await Started("VNyan");   // no platform has a category for it (its search's best guess does not count)
        Assert.Empty(_requests.Pending);

        _twitch.Current = "Helldivers 2";
        _kick.Current = "HELLDIVERS 2";
        await Started("Helldivers 2");
        Assert.Empty(_requests.Pending);
    }

    [Fact]
    public async Task Always_switches_without_asking_never_stays_quiet_and_a_decline_changes_nothing()
    {
        await Started("Helldivers 2");
        await _requests.AnswerAsync(_requests.Pending[0].Id, RequestAnswer.AcceptAndRemember, "kris");
        Assert.Equal(["Helldivers 2"], _switcher.Settings.AlwaysSwitch);

        _twitch.Current = _kick.Current = "Just Chatting";
        await Started("Helldivers 2");
        Assert.Empty(_requests.Pending);
        Assert.Equal(2, _twitch.Set.Count);

        _switcher.UpdateSettings(s => s.NeverAsk = ["Just Chatting"]);
        _twitch.Current = "Helldivers 2";
        await Started("Just Chatting");
        Assert.Empty(_requests.Pending);

        _switcher.UpdateSettings(s => s.AlwaysSwitch = []);
        _twitch.Current = _kick.Current = "Just Chatting";
        await Started("Helldivers 2");
        Assert.Equal(RequestOutcome.Declined, (await _requests.AnswerAsync(_requests.Pending[0].Id, RequestAnswer.Decline, "kris")).Result);
        Assert.Equal(2, _twitch.Set.Count);
    }

    [Fact]
    public async Task Closing_the_game_withdraws_the_request_and_asks_to_switch_back_when_set()
    {
        await Started("Helldivers 2");
        await Stopped("Helldivers 2");
        Assert.Empty(_requests.Pending);   // nobody answered: nothing to switch back from

        _switcher.UpdateSettings(s => s.ReturnCategory = "Just Chatting");
        await Started("Helldivers 2");
        await _requests.AnswerAsync(_requests.Pending[0].Id, RequestAnswer.Accept, "kris");
        await Stopped("Helldivers 2");
        var back = Assert.Single(_requests.Pending);
        Assert.Equal("Helldivers 2 closed. Switch the stream category back to Just Chatting?", back.Title);
        await _requests.AnswerAsync(back.Id, RequestAnswer.Accept, "kris");
        Assert.Equal("Just Chatting", _twitch.Current);
    }

    [Fact]
    public async Task Platforms_turned_off_or_signed_out_are_left_out_and_a_failure_is_reported()
    {
        _switcher.UpdateSettings(s => { s.YouTube = true; s.Kick = false; });
        _twitch.CanSet = false;
        await Started("Helldivers 2");
        Assert.Equal("YouTube: Gaming", Assert.Single(_requests.Pending).Detail);

        _switcher.UpdateSettings(s => { s.Kick = true; s.YouTube = false; });
        _kick.Fail = true;
        await Started("Helldivers 2");
        var outcome = await _requests.AnswerAsync(_requests.Pending[0].Id, RequestAnswer.Accept, "kris");
        Assert.Equal(RequestOutcome.Failed, outcome.Result);
        Assert.Contains("Kick said no", outcome.Message);
    }

    [Fact]
    public async Task An_unanswered_request_expires_and_a_new_one_replaces_the_old()
    {
        await Started("Helldivers 2");
        var first = _requests.Pending[0];
        await Started("Helldivers 2");
        Assert.NotEqual(first.Id, Assert.Single(_requests.Pending).Id);

        _clock.Advance(TimeSpan.FromMinutes(11).TotalSeconds);
        _requests.Tick();
        Assert.Empty(_requests.Pending);
        Assert.Empty(_twitch.Set);
    }

    [Fact]
    public void Similar_names_ignore_case_spaces_and_symbols_but_not_other_games()
    {
        Assert.True(CategoryNames.Similar("HELLDIVERS™ 2", "Helldivers 2"));
        Assert.True(CategoryNames.Similar("Counter-Strike", "Counter-Strike 2"));
        Assert.False(CategoryNames.Similar("Just Chatting", "VNyan"));
        Assert.False(CategoryNames.Similar("", "x"));
    }
}
