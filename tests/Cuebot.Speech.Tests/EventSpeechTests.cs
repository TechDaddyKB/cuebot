using Cuebot.Speech.Tests.Fakes;
using static Cuebot.Speech.Tests.Fakes.ChatEvents;

namespace Cuebot.Speech.Tests;

public class EventSpeechTests
{
    private static async Task<SpeechHarness> StartAsync()
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.Events.Enabled = true;
        return h;
    }

    [Theory]
    [InlineData("Follow", "Alice", "Alice followed", "", "", "Alice just followed!")]
    [InlineData("Subscription", "Alice", "Alice resubscribed for 5 months", "love it", "", "Alice subscribed! love it")]
    [InlineData("GiftSubscription", "Alice", "Alice gifted 5 tier 1000 subs", "", "", "Alice gifted 5 subs!")]
    [InlineData("Tip", "Alice", "Alice cheered 100 bits", "take my bits", "100 bits", "Alice cheered 100 bits! take my bits")]
    [InlineData("Tip", "Alice", "Alice sent a $5.00 rant", "great show", "$5.00", "Alice tipped $5.00! great show")]
    [InlineData("Raid", "Alice", "Alice raided with 42 viewers", "", "", "Alice is raiding with 42 viewers!")]
    [InlineData("Redemption", "Alice", "Alice redeemed Hydrate", "drink water", "", "Alice redeemed Hydrate. drink water")]
    public async Task Default_templates_per_kind(string kind, string user, string title, string detail, string amount, string expected)
    {
        await using var h = await StartAsync();

        h.Bus.Publish(Activity(kind, user, title, detail, amount));
        await h.DrainAsync();

        Assert.Equal([expected], h.Spoken);
    }

    [Fact]
    public async Task Stream_online_and_offline_are_off_by_default()
    {
        await using var h = await StartAsync();

        h.Bus.Publish(Activity("StreamOnline", "", "Stream went live"));
        h.Bus.Publish(Activity("Other", "x", "something"));
        await h.DrainAsync();

        Assert.Empty(h.Spoken);
    }

    [Fact]
    public async Task Nothing_is_spoken_when_event_speech_is_off()
    {
        await using var h = await SpeechHarness.StartAsync();

        h.Bus.Publish(Activity("Follow", "Alice", "Alice followed"));
        await h.DrainAsync();

        Assert.Empty(h.Spoken);
    }

    [Fact]
    public async Task Custom_templates_use_variables_weights_and_their_alias()
    {
        await using var h = await StartAsync();
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Hype", EngineId = "fake", VoiceId = "hype" });
        h.Settings.Events.Alias = "Main";
        h.Settings.Events.Templates.Add(new EventTemplate
        {
            Kind = "Raid",
            Alias = "Hype",
            Messages =
            [
                new EventMessage { Text = "never", Enabled = false, Weight = 100 },
                new EventMessage { Text = "%name% brought %viewers% friends from %platform%", Weight = 1 },
            ],
        });

        h.Bus.Publish(Activity("Raid", "Bob", "Bob raided", extra: new Dictionary<string, object?> { ["viewers"] = 12 }));
        h.Bus.Publish(Activity("Follow", "Carl", "Carl followed"));
        await h.DrainAsync();

        Assert.Equal(["Bob brought 12 friends from Twitch", "Carl just followed!"], h.Spoken);
        Assert.Equal(["hype", "v1"], h.Engine.History.Take(2).Select(c => c.VoiceId));
    }

    [Fact]
    public void Weighted_pick_follows_the_weights()
    {
        var speech = new EventSpeech(new Random(7));
        var settings = new EventSpeechSettings
        {
            SuppressDuplicateFollows = false,
            Templates =
            [
                new EventTemplate
                {
                    Kind = "Follow",
                    Messages = [new EventMessage { Text = "a", Weight = 9 }, new EventMessage { Text = "b", Weight = 1 }],
                },
            ],
        };
        var args = Activity("Follow", "x", "x followed").Args;

        var picks = Enumerable.Range(0, 1000).Select(_ => speech.Build(args, settings, new FilterProfile(), new SpeakChatSettings())!.Text).ToArray();

        Assert.InRange(picks.Count(p => p == "a"), 850, 950);
    }

    [Fact]
    public async Task Disabled_templates_and_minimums()
    {
        await using var h = await StartAsync();
        h.Settings.Events.Templates.Add(new EventTemplate { Kind = "Follow", Enabled = false });
        h.Settings.Events.MinRaidViewers = 10;
        h.Settings.Events.MinCheerBits = 100;
        h.Settings.Events.MinTipAmount = 5;

        h.Bus.Publish(Activity("Follow", "A", "A followed"));
        h.Bus.Publish(Activity("Raid", "B", "B raided with 9 viewers"));
        h.Bus.Publish(Activity("Raid", "C", "C raided with 10 viewers"));
        h.Bus.Publish(Activity("Tip", "D", "D cheered 99 bits", amount: "99 bits"));
        h.Bus.Publish(Activity("Tip", "E", "E cheered 100 bits", amount: "100 bits"));
        h.Bus.Publish(Activity("Tip", "F", "F tipped", amount: "$4.99"));
        h.Bus.Publish(Activity("Tip", "G", "G tipped", amount: "€5,00"));
        await h.DrainAsync();

        Assert.Equal(["C is raiding with 10 viewers!", "E cheered 100 bits!", "G tipped €5,00!"], h.Spoken);
    }

    [Fact]
    public async Task Duplicate_follows_are_spoken_once_per_session()
    {
        await using var h = await StartAsync();

        h.Bus.Publish(Activity("Follow", "Alice", "Alice followed"));
        h.Bus.Publish(Activity("Follow", "alice", "alice followed"));
        h.Service.ResetSession();
        h.Bus.Publish(Activity("Follow", "Alice", "Alice followed"));
        await h.DrainAsync();

        Assert.Equal(2, h.Spoken.Count);
    }

    [Fact]
    public async Task Viewer_text_in_events_is_filtered()
    {
        await using var h = await StartAsync();
        h.Settings.Filters.BadWords.Add("darn");
        h.Settings.Filters.BadWordAction = BadWordAction.Strip;

        h.Bus.Publish(Activity("Subscription", "Alice", "Alice subscribed", detail: "darn good stream www.spam.com"));
        await h.DrainAsync();

        Assert.Equal(["Alice subscribed! good stream link"], h.Spoken);
    }

    [Fact]
    public void Variables_cover_the_speakerbot_names()
    {
        var variables = EventSpeech.Variables(Activity("Subscription", "Alice", "Alice subscribed", extra: new Dictionary<string, object?>
        {
            ["tier"] = "2",
            ["cumulativeMonths"] = 7,
            ["recipient"] = "Bob",
            ["currency"] = "USD",
            ["level"] = 3,
            ["percent"] = 50,
            ["cost"] = 100,
        }).Args);

        foreach (var name in EventSpeech.VariableNames) Assert.True(variables.ContainsKey(name), name);
        Assert.Equal(("Alice", "2", "2", "7", "Bob", "USD"),
            ((string)variables["name"]!, (string)variables["tier"]!, (string)variables["subtier"]!, (string)variables["cumulative"]!,
                (string)variables["recipient"]!, (string)variables["currency"]!));
    }

    [Fact]
    public async Task Events_are_dropped_when_speech_is_disabled()
    {
        await using var h = await StartAsync();
        h.Service.Enabled = false;

        h.Bus.Publish(Activity("Follow", "Alice", "Alice followed"));
        h.Service.Enabled = true;
        await h.DrainAsync();

        Assert.Empty(h.Spoken);
    }
}
