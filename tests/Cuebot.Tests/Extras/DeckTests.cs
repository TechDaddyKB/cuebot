using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Decks;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

/// <summary>Cuebot Decks: the service's views and runtime state, pressing, and Streamer.bot's 10 deck sub-actions.</summary>
public sealed class DeckTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly DeckService _decks;
    private readonly DeckDefinition _deck;
    private readonly List<DeckMessage> _messages = [];

    public DeckTests()
    {
        _bus = new EventBus(_log);
        _decks = new DeckService(null, _bus, _log);
        _deck = new DeckDefinition
        {
            Name = "Main",
            Rows = 2,
            Columns = 3,
            Arguments = "deckArg=1",
            Pages =
            [
                new DeckPage
                {
                    Name = "Scenes",
                    Items =
                    [
                        new DeckItem
                        {
                            Name = "Live", Row = 0, Column = 1, Action = "Go live", Arguments = "scene=Main",
                            States = [new DeckItemState { Title = "Go", Background = "#333", Icon = "mdi:play" }, new DeckItemState { Title = "Stop", Background = "#a00" }],
                        },
                        new DeckItem { Name = "Mute", Row = 1, Column = 2, ToggleOnPress = true },
                    ],
                },
                new DeckPage { Name = "Sounds", Items = [new DeckItem { Name = "Horn" }] },
            ],
        };
        _decks.Save(_deck);
        _decks.Message += m => _messages.Add(m);
    }

    private JsonObject Item(string name, JsonObject? view = null) =>
        (view ?? _decks.View(_deck.Id)!)["items"]!.AsArray().Cast<JsonObject>().First(i => (string)i["name"]! == name);

    [Fact]
    public void View_shows_the_current_page_with_state_fallbacks()
    {
        var view = _decks.View(_deck.Id)!;
        Assert.Equal(("Main", 2, 3, 0, 2, "Scenes"), ((string)view["name"]!, (int)view["rows"]!, (int)view["columns"]!, (int)view["page"]!,
            (int)view["pageCount"]!, (string)view["pageName"]!));
        var live = Item("Live", view);
        Assert.Equal(("Go", "#333", "mdi:play", 0), ((string)live["title"]!, (string)live["background"]!, (string)live["icon"]!, (int)live["state"]!));

        _decks.SetState("Main", "live", 1);
        live = Item("Live");
        // State 1 has its own title and background; its icon falls back to state 0's.
        Assert.Equal(("Stop", "#a00", "mdi:play", 1), ((string)live["title"]!, (string)live["background"]!, (string)live["icon"]!, (int)live["state"]!));
        Assert.Equal("state", _messages.Last().Kind);

        var disabled = Json(_deck);
        disabled.Enabled = false;
        _decks.Save(disabled);
        Assert.Null(_decks.View(_deck.Id));
    }

    [Fact]
    public void Pressing_fires_the_trigger_event_with_deck_arguments_and_toggles()
    {
        var events = new EventRecorder(_bus, DeckEvents.ItemPressed);
        var mute = _deck.Pages[0].Items[1];
        Assert.True(_decks.Press(_deck.Id, mute.Id, "10.0.0.5"));
        var e = Assert.Single(events.Events);
        Assert.Equal(("Main", "Mute", 1, 0, "10.0.0.5", "1"), (e.Args["deckName"], e.Args["deckItemName"], e.Args["deckItemState"],
            e.Args["deckPage"], e.Args["deckClient"], e.Args["deckArg"]));
        Assert.Equal(1, e.Args["deckPageNumber"]); // counts from 1, like the page sub-actions
        Assert.Equal(1, (int)Item("Mute")["state"]!);
        Assert.False(_decks.Press(_deck.Id, Guid.NewGuid()));
        Assert.False(_decks.Press(Guid.NewGuid(), mute.Id));
    }

    [Fact]
    public async Task Pressing_runs_the_item_action_with_its_arguments()
    {
        var profile = new ProfileService(null, _log);
        var capture = new CaptureArgs();
        using var engine = new ActionEngine(_bus, profile, new Registry([capture], []), new GlobalVariables(null, _log), _log);
        var decks = new DeckService(null, _bus, _log, () => engine);
        decks.Save(_deck);
        profile.Profile.Actions.Add(new ActionDefinition { Name = "Go live", SubActions = [new SubActionDefinition { Type = CaptureArgs.Id }] });
        profile.NotifyChanged();
        var completed = new TaskCompletionSource<ActionRunRecord>();
        engine.RunCompleted += r => completed.TrySetResult(r);

        Assert.True(decks.Press(_deck.Id, _deck.Pages[0].Items[0].Id));
        var run = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Deck: Main", run.Source);
        Assert.Equal(("Main", "1", "Live"), (capture.Args!["scene"], capture.Args["deckArg"], capture.Args["deckItemName"]));
    }

    [Fact]
    public async Task Item_sub_actions_change_the_look_per_state()
    {
        var p = new Dictionary<string, string> { ["deck"] = "Main", ["item"] = "Live" };
        Dictionary<string, string> With(params (string K, string V)[] extra)
        {
            var d = new Dictionary<string, string>(p);
            foreach (var (k, v) in extra) d[k] = v;
            return d;
        }

        Assert.True((await SubActionHarness.RunAsync(new DeckItemSetTitleSubAction(_decks), With(("title", "On air"), ("color", "red")))).Success);
        Assert.True((await SubActionHarness.RunAsync(new DeckItemSetBackgroundSubAction(_decks), With(("color", "#0f0"), ("url", "http://img/x.png")))).Success);
        Assert.True((await SubActionHarness.RunAsync(new DeckItemSetIconSubAction(_decks), With(("name", "mdi:stop"), ("color", "#fff")))).Success);
        Assert.True((await SubActionHarness.RunAsync(new DeckItemSetValueSubAction(_decks), With(("value", "42")))).Success);
        var live = Item("Live");
        Assert.Equal(("On air", "red", "#0f0", "http://img/x.png", "mdi:stop", "#fff", "42"),
            ((string)live["title"]!, (string)live["titleColor"]!, (string)live["background"]!, (string)live["backgroundImage"]!,
                (string)live["icon"]!, (string)live["iconColor"]!, (string)live["value"]!));

        // A change to state 1 only shows while state 1 is active.
        await SubActionHarness.RunAsync(new DeckItemSetTitleSubAction(_decks), With(("title", "Ending"), ("state", "1")));
        Assert.Equal("On air", (string)Item("Live")["title"]!);
        var toggled = await SubActionHarness.RunAsync(new DeckItemToggleStateSubAction(_decks), With());
        Assert.Equal(1, toggled.Args["deckItemState"]);
        Assert.Equal("Ending", (string)Item("Live")["title"]!);
        await SubActionHarness.RunAsync(new DeckItemSetStateSubAction(_decks), With(("state", "0")));
        Assert.Equal(0, (int)Item("Live")["state"]!);

        Assert.False((await SubActionHarness.RunAsync(new DeckItemSetStateSubAction(_decks), new() { ["deck"] = "Main", ["item"] = "Nope" })).Success);
        Assert.False((await SubActionHarness.RunAsync(new DeckItemSetStateSubAction(_decks), new() { ["deck"] = "Other", ["item"] = "Live" })).Success);

        _decks.ResetRuntime(_deck.Id);
        Assert.Equal("Go", (string)Item("Live")["title"]!);
    }

    [Fact]
    public async Task Page_sub_actions_wrap_and_clamp()
    {
        var deck = new Dictionary<string, string> { ["deck"] = _deck.Id.ToString() };
        Assert.Equal(2, (await SubActionHarness.RunAsync(new DeckPageNextSubAction(_decks), deck)).Args["deckPageNumber"]);
        Assert.Equal("Sounds", (string)_decks.View(_deck.Id)!["pageName"]!);
        Assert.Equal(1, (await SubActionHarness.RunAsync(new DeckPageNextSubAction(_decks), deck)).Args["deckPageNumber"]);
        Assert.Equal(2, (await SubActionHarness.RunAsync(new DeckPagePreviousSubAction(_decks), deck)).Args["deckPageNumber"]);
        Assert.Equal(1, (await SubActionHarness.RunAsync(new DeckPageSetSubAction(_decks), new(deck) { ["page"] = "1" })).Args["deckPageNumber"]);
        Assert.Equal(2, (await SubActionHarness.RunAsync(new DeckPageSetSubAction(_decks), new(deck) { ["page"] = "99" })).Args["deckPageNumber"]);
    }

    [Fact]
    public async Task Deck_notify_sends_a_notification_message()
    {
        Assert.True((await SubActionHarness.RunAsync(new DeckNotifySubAction(_decks),
            new() { ["deck"] = "main", ["title"] = "Raid!", ["description"] = "50 viewers" })).Success);
        var m = _messages.Last();
        Assert.Equal(("notify", "Raid!", "50 viewers", _deck.Id), (m.Kind, (string)m.Data["title"]!, (string)m.Data["description"]!, m.DeckId));
    }

    [Fact]
    public void Pressed_trigger_filters_by_deck_and_item()
    {
        var trigger = new DeckTriggers(_decks).GetTriggers().Single();
        var e = new BotEvent(DeckEvents.ItemPressed, new Dictionary<string, object?>
        {
            ["deckName"] = "Main", ["deckId"] = _deck.Id.ToString(), ["deckItemName"] = "Live", ["deckItemId"] = "x",
        });
        bool Matches(string deck, string item) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["deck"] = deck, ["item"] = item }), new Dictionary<string, object?>());
        Assert.True(Matches("", ""));
        Assert.True(Matches("main", "live"));
        Assert.True(Matches(_deck.Id.ToString(), ""));
        Assert.False(Matches("Other", ""));
        Assert.False(Matches("", "Mute"));
    }

    [Fact]
    public void Saving_normalizes_and_removing_forgets_the_deck()
    {
        var bad = new DeckDefinition { Name = "Bad", Rows = 0, Columns = 99, Pages = [] };
        _decks.Save(bad);
        var saved = _decks.Find("bad")!;
        Assert.Equal((1, 20, 1), (saved.Rows, saved.Columns, saved.Pages.Count));
        Assert.True(_decks.Remove(bad.Id));
        Assert.Null(_decks.Find("Bad"));
        Assert.False(_decks.Remove(bad.Id));
    }

    private static DeckDefinition Json(DeckDefinition d) => Cuebot.Core.Storage.Json.Clone(d);

    public void Dispose() => _log.Dispose();

    private sealed class CaptureArgs : ISubActionHandler
    {
        public const string Id = "test.captureDeck";
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => Id;
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
