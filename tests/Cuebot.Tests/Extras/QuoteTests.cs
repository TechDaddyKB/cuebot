using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Quotes;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class QuoteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-quotes-").FullName;
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly EventRecorder _events;

    public QuoteTests()
    {
        _bus = new EventBus(_log);
        _events = new EventRecorder(_bus, QuoteEvents.Added, QuoteEvents.Removed, QuoteEvents.Shown, QuoteEvents.Updated);
    }

    private QuoteStore Store(int seed = 1) => new(new AppPaths(_dir), _log, _bus, _clock, new Random(seed));

    [Fact]
    public void Crud_persists_and_keeps_ids_unique()
    {
        using (var store = Store())
        {
            var a = store.Add("  Never gonna give you up  ", "@Alice", "1", "twitch", "Just Chatting");
            var b = store.Add("Second", "Bob");
            Assert.Equal((1, 2), (a.Id, b.Id));
            Assert.Equal("Never gonna give you up", a.Text);
            Assert.Equal("Alice", a.User);
            Assert.Equal(_clock.Now, a.CreatedAt);

            Assert.NotNull(store.Update(2, text: "Second, edited", game: "Tetris"));
            Assert.Null(store.Update(99, text: "nope"));
            Assert.Equal("Second, edited", store.Delete(2)?.Text);
            var c = store.Add("Third");
            Assert.Equal(3, c.Id);   // ids are not reused after a delete
            store.Flush();
        }

        using var reloaded = Store();
        Assert.Equal([1, 3], reloaded.All().Select(q => q.Id));
        Assert.Equal("Just Chatting", reloaded.Get(1)!.Game);
        Assert.Equal(4, reloaded.Add("Fourth").Id);

        Assert.Equal([QuoteEvents.Added, QuoteEvents.Added, QuoteEvents.Updated, QuoteEvents.Removed, QuoteEvents.Added, QuoteEvents.Added],
            _events.Events.Select(e => e.Type));
        var removedEvent = _events.OfType(QuoteEvents.Removed).Single();
        Assert.Equal(2, removedEvent.Args["quoteId"]);
        Assert.Equal("Second, edited", removedEvent.Args["quote"]);
    }

    [Fact]
    public void Empty_text_is_rejected()
    {
        using var store = Store();
        Assert.Throws<ArgumentException>(() => store.Add("   "));
        store.Add("ok");
        Assert.Throws<ArgumentException>(() => store.Update(1, text: " "));
    }

    [Fact]
    public void Search_random_and_reindex()
    {
        using var store = Store(seed: 7);
        store.Add("The cake is a lie", "GLaDOS", game: "Portal");
        store.Add("Do a barrel roll", "Peppy", game: "Star Fox");
        store.Add("It's dangerous to go alone", "Old man", game: "Zelda");
        Assert.Single(store.Search("barrel"));
        Assert.Single(store.Search("glados"));
        Assert.Single(store.Search("zelda"));
        Assert.Equal(2, Assert.Single(store.Search("#2")).Id);
        Assert.Equal(3, store.Search("").Count);

        var seen = Enumerable.Range(0, 60).Select(_ => store.RandomQuote()!.Id).ToHashSet();
        Assert.Equal([1, 2, 3], seen.Order());
        Assert.Equal(1, store.RandomQuote("cake")!.Id);
        Assert.Null(store.RandomQuote("no such words"));

        store.Delete(1);
        Assert.Equal(2, store.Reindex());
        Assert.Equal([1, 2], store.All().Select(q => q.Id));
        Assert.Equal(3, store.Add("new").Id);
    }

    [Fact]
    public void Remembers_the_current_game_from_platform_events()
    {
        using var store = Store();
        _bus.Publish(new BotEvent("twitch.streamUpdate", new Dictionary<string, object?> { ["categoryName"] = "Elden Ring" }));
        Assert.Equal("Elden Ring", store.Add("Try finger but hole").Game);
        Assert.Equal("Chess", store.Add("Checkmate", game: "Chess").Game);
    }

    [Fact]
    public void Json_and_csv_round_trip()
    {
        using var store = Store();
        store.Add("Plain");
        store.Add("Has, comma and \"quotes\"\nand a line break", "Ann, the user", "7", "youtube", "Café");
        var all = store.All();

        var json = QuoteTransfer.ToJson(all);
        var fromJson = QuoteTransfer.FromJson(json);
        Assert.Equal(all.Select(q => (q.Id, q.Text, q.User, q.Game, q.CreatedAt)), fromJson.Select(q => (q.Id, q.Text, q.User, q.Game, q.CreatedAt)));

        var csv = QuoteTransfer.ToCsv(all);
        Assert.StartsWith("id,text,user,userId,platform,game,createdAt", csv);
        var fromCsv = QuoteTransfer.FromCsv(csv);
        Assert.Equal(all.Select(q => (q.Id, q.Text, q.User, q.UserId, q.Platform, q.Game, q.CreatedAt)),
            fromCsv.Select(q => (q.Id, q.Text, q.User, q.UserId, q.Platform, q.Game, q.CreatedAt)));

        // A whole quotes.json document imports too; plain spreadsheets only need a text column.
        Assert.Equal(2, QuoteTransfer.FromJson("{\"quotes\":[{\"text\":\"a\"},{\"text\":\"b\"}]}").Count);
        var simple = QuoteTransfer.FromCsv("Quote,Game\r\nhello world,Doom\r\n,\r\n");
        Assert.Equal("hello world", Assert.Single(simple).Text);
        Assert.Throws<FormatException>(() => QuoteTransfer.FromCsv("a,b\n1,2"));
        Assert.Throws<FormatException>(() => QuoteTransfer.FromJson("not json"));
    }

    [Fact]
    public void Import_appends_or_replaces()
    {
        using var store = Store();
        store.Add("existing");
        var incoming = new[] { new Quote { Id = 10, Text = "ten" }, new Quote { Id = 10, Text = "dup id" }, new Quote { Text = " " } };

        Assert.Equal(2, store.Import(incoming, replace: false));
        Assert.Equal([1, 2, 3], store.All().Select(q => q.Id));
        Assert.All(store.All(), q => Assert.NotEqual(default, q.CreatedAt));

        Assert.Equal(2, store.Import(incoming, replace: true));
        Assert.Equal([(10, "ten"), (11, "dup id")], store.All().Select(q => (q.Id, q.Text)));
        Assert.Equal(12, store.Add("next").Id);
    }

    [Fact]
    public async Task Sub_actions_write_quote_arguments()
    {
        using var store = Store();
        var caller = new Dictionary<string, object?>
        {
            ["user"] = "Alice", ["userId"] = "1", ["platform"] = "twitch", ["input"] = "I never said that", ["input0"] = "I",
        };
        var added = await SubActionHarness.RunAsync(new AddQuoteSubAction(store), [], caller);
        Assert.True(added.Success);
        Assert.Equal(1, added.Args["quoteId"]);
        Assert.Equal("I never said that", added.Args["quote"]);
        Assert.Equal("Alice", added.Args["quoteUser"]);
        Assert.Equal("twitch", added.Args["quotePlatform"]);
        Assert.Equal("2026-01-05", added.Args["quoteDate"]);
        store.Add("Another one", "Bob", game: "Doom");

        var byId = await SubActionHarness.RunAsync(new GetQuoteSubAction(store), new() { ["id"] = "#2" });
        Assert.Equal(true, byId.Args["quoteFound"]);
        Assert.Equal("Another one", byId.Args["quote"]);
        Assert.Equal("Doom", byId.Args["quoteGame"]);
        Assert.Equal(2, byId.Args["quoteCount"]);
        Assert.Single(_events.OfType(QuoteEvents.Shown));

        // No %input0% in a non-command run: the placeholder stays unresolved and a random quote is picked.
        var random = await SubActionHarness.RunAsync(new GetQuoteSubAction(store), new() { ["search"] = "never" });
        Assert.Equal(1, random.Args["quoteId"]);

        var missing = await SubActionHarness.RunAsync(new GetQuoteSubAction(store), new() { ["id"] = "42", ["fireShown"] = "false" });
        Assert.Equal(false, missing.Args["quoteFound"]);
        Assert.Equal("", missing.Args["quote"]);
        Assert.Equal(2, _events.OfType(QuoteEvents.Shown).Count);

        var edited = await SubActionHarness.RunAsync(new EditQuoteSubAction(store), new() { ["id"] = "1", ["game"] = "Minecraft" });
        Assert.Equal(true, edited.Args["quoteUpdated"]);
        Assert.Equal("I never said that", edited.Args["quote"]);
        Assert.Equal("Minecraft", store.Get(1)!.Game);

        var deleted = await SubActionHarness.RunAsync(new DeleteQuoteSubAction(store), [], new() { ["input0"] = "2" });
        Assert.Equal(true, deleted.Args["quoteDeleted"]);
        Assert.Equal(1, deleted.Args["quoteCount"]);

        var bad = await SubActionHarness.RunAsync(new DeleteQuoteSubAction(store), new() { ["id"] = "abc" });
        Assert.False(bad.Success);
        Assert.Contains(bad.Log, l => l.Message.Contains("not a quote id"));

        var count = await SubActionHarness.RunAsync(new GetQuoteCountSubAction(store), []);
        Assert.Equal(1, count.Args["quoteCount"]);
    }

    [Fact]
    public void Triggers_filter_by_user()
    {
        using var store = Store();
        var trigger = new QuoteTriggers(store).GetTriggers().Single(t => t.TypeId == "quotes.added");
        var e = new BotEvent(QuoteEvents.Added, QuoteStore.ToArgs(new Quote { Id = 1, Text = "x", User = "Alice" }));
        bool Match(string user) => trigger.Matches(e, new Core.Actions.TriggerDefinition(),
            Core.Actions.ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["user"] = user }), new Dictionary<string, object?>());
        Assert.True(Match(""));
        Assert.True(Match("@alice"));
        Assert.False(Match("bob"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
