using System.Text.Json;
using Cuebot.Core.Storage;
using Cuebot.Speech.Tests.Fakes;
using static Cuebot.Speech.Tests.Fakes.ChatEvents;

namespace Cuebot.Speech.Tests;

public class AliasTests
{
    private readonly FakeTtsEngine _a = new("a", available: true, "a1", "a2");
    private readonly FakeTtsEngine _off = new("off", available: false, "x");

    private static VoiceAlias Multi(VoiceSelection selection, params AliasVoice[] voices) =>
        new() { Name = "Crowd", Selection = selection, Voices = [.. voices], Rate = 1, Pitch = -1, Volume = 50, DeviceId = "dev" };

    [Fact]
    public void Round_robin_cycles_through_the_voices()
    {
        var resolver = new VoiceResolver([_a]);
        var alias = Multi(VoiceSelection.RoundRobin,
            new AliasVoice { EngineId = "a", VoiceId = "1" }, new AliasVoice { EngineId = "a", VoiceId = "2" }, new AliasVoice { EngineId = "a", VoiceId = "3" });

        var picks = Enumerable.Range(0, 5).Select(_ => resolver.Resolve(alias).VoiceId).ToArray();

        Assert.Equal(["1", "2", "3", "1", "2"], picks);
    }

    [Fact]
    public void Random_selection_follows_weights_and_skips_unavailable_engines()
    {
        var resolver = new VoiceResolver([_a, _off], new Random(42));
        var alias = Multi(VoiceSelection.Random,
            new AliasVoice { EngineId = "a", VoiceId = "often", Weight = 3 },
            new AliasVoice { EngineId = "a", VoiceId = "rare", Weight = 1 },
            new AliasVoice { EngineId = "off", VoiceId = "never", Weight = 100 });

        var picks = Enumerable.Range(0, 2000).Select(_ => resolver.Resolve(alias).VoiceId).ToArray();

        Assert.DoesNotContain("never", picks);
        Assert.InRange(picks.Count(p => p == "often"), 1350, 1650);
    }

    [Fact]
    public void Voice_values_combine_with_the_alias_values()
    {
        var resolver = new VoiceResolver([_a]);
        var alias = Multi(VoiceSelection.Random, new AliasVoice { EngineId = "a", VoiceId = "1", Rate = 9, Pitch = 3, Volume = 80, Style = "sad" });

        var voice = resolver.Resolve(alias);

        Assert.Equal((10, 2, 40, "sad", "dev", "Crowd"), (voice.Rate, voice.Pitch, voice.Volume, voice.Style, voice.DeviceId, voice.AliasName));
    }

    [Fact]
    public void All_voices_unavailable_reports_the_reason()
    {
        var resolver = new VoiceResolver([_off]);
        var error = Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(Multi(VoiceSelection.Random, new AliasVoice { EngineId = "off", VoiceId = "x" })));
        Assert.Contains("fake engine disabled", error.Message);
    }

    [Fact]
    public void Old_single_voice_json_still_loads()
    {
        const string json = """
            {"elevenLabsApiKey":"k","aliases":[{"name":"Narrator","engineId":"windows","voiceId":"Zira","rate":3,"volume":70}],
             "speakChat":{"enabled":true,"alias":"Narrator","ignoreCommands":true,"maxLength":100},"masterVolume":90}
            """;

        var settings = JsonSerializer.Deserialize<SpeechSettings>(json, Json.Options)!;

        var alias = Assert.Single(settings.Aliases);
        var voice = Assert.Single(alias.EffectiveVoices());
        Assert.Equal(("windows", "Zira"), (voice.EngineId, voice.VoiceId));
        Assert.Equal((3, 70, 0, VoiceSelection.Random), (alias.Rate, alias.Volume, alias.Pitch, alias.Selection));
        Assert.True(settings.Enabled);
        Assert.Equal(FilterProfile.DefaultName, settings.Filters.Name);
        Assert.Null(settings.Filters.IgnoredPrefixes);
        Assert.Equal("!tts", settings.Commands.Prefix);
        Assert.Equal(90, settings.MasterVolume);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new SpeechSettings();
        settings.Aliases.Add(Multi(VoiceSelection.RoundRobin, new AliasVoice { EngineId = "a", VoiceId = "1" }));
        settings.Engines.Polly.AccessKeyId = "AK";
        settings.Commands.Permissions["pause"] = ChatAudience.Vips | ChatAudience.Moderators;
        settings.Events.Templates.Add(new EventTemplate { Kind = "Follow", Messages = [new EventMessage { Text = "hi" }] });

        var copy = Json.Clone(settings);

        Assert.Equal(VoiceSelection.RoundRobin, copy.Aliases[0].Selection);
        Assert.Equal("AK", copy.Engines.Polly.AccessKeyId);
        Assert.Equal(ChatAudience.Vips | ChatAudience.Moderators, copy.Commands.Permissions["pause"]);
        Assert.Equal("hi", copy.Events.Templates[0].Messages[0].Text);
    }

    [Fact]
    public async Task Random_voices_skip_the_ignore_profile_and_stick_per_user()
    {
        var engine = new FakeTtsEngine("fake")
        {
            VoiceInfos = [new VoiceInfo("de1", "German", "de-DE"), new VoiceInfo("en1", "English", "en-US"), new VoiceInfo("en2", "Other", "en-GB")],
        };
        await using var h = new SpeechHarness(engine);
        await h.Service.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.RandomVoices = true;
        h.Settings.VoiceIgnoreProfiles.Add(new VoiceIgnoreProfile { Name = "p", Locales = ["de"], Voices = ["fake|en2"] });
        h.Settings.ActiveVoiceIgnoreProfile = "p";

        for (var i = 0; i < 5; i++) await h.SayAsync(Message("Alice", $"m{i}"));
        await h.DrainAsync();

        Assert.All(h.Engine.History.Where(c => c.Text != "marker"), c => Assert.Equal("en1", c.VoiceId));
        Assert.Equal("fake|en1", h.Users.Find("Twitch", "alice-id", null)!.RandomVoice);
    }

    [Fact]
    public void Ignore_profile_matches_voices_and_locale_prefixes()
    {
        var profile = new VoiceIgnoreProfile { Voices = ["Zira"], Locales = ["en-GB"] };
        var engine = new FakeTtsEngine("windows");

        Assert.True(VoiceResolver.IsIgnored(engine, new VoiceInfo("Zira", "Zira", "en-US"), profile));
        Assert.True(VoiceResolver.IsIgnored(engine, new VoiceInfo("x", "x", "en-GB"), profile));
        Assert.False(VoiceResolver.IsIgnored(engine, new VoiceInfo("y", "y", "en-US"), profile));
        Assert.False(VoiceResolver.IsIgnored(engine, new VoiceInfo("Zira", "Zira", "en-US"), null));
    }

    [Fact]
    public async Task Default_alias_is_used_when_none_is_given()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Fallback", EngineId = "fake", VoiceId = "fb" });
        h.Settings.DefaultAlias = "fallback";

        await h.Service.SpeakAsync("x", wait: true);
        await h.Service.SpeakAsync("y", "unknown alias", wait: true);

        Assert.Equal(["fb", "fb"], h.Engine.History.Select(c => c.VoiceId));
    }

    [Fact]
    public async Task Alias_pitch_and_style_reach_the_engine()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Aliases[0].Pitch = 4;
        h.Settings.Aliases[0].Style = "cheerful";

        await h.Service.SpeakAsync("styled", wait: true);

        Assert.Equal(new SynthesisCall("styled", "v1", 0) { Pitch = 4, Style = "cheerful" }, h.Engine.History.Single());
    }
}

public class UserSettingsTests
{
    private static async Task<SpeechHarness> StartAsync()
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%user% says %message%";
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Robot", EngineId = "fake", VoiceId = "robot", Rate = 2, Pitch = 2 });
        return h;
    }

    [Fact]
    public async Task Assigned_alias_nickname_and_overrides_apply_to_chat()
    {
        await using var h = await StartAsync();
        h.Service.SetUserVoice("Twitch", "bob-id", "Bob", "robot");
        h.Users.Update("Twitch", "bob-id", "Bob", u =>
        {
            u.Nickname = "Bobby";
            u.Rate = -5;
        });

        await h.SayAsync(Message("Bob", "hello"));
        await h.SayAsync(Message("Carl", "hi"));
        await h.DrainAsync();

        Assert.Equal(new SynthesisCall("Bobby says hello", "robot", -5) { Pitch = 2 }, h.Engine.History[0]);
        Assert.Equal(new SynthesisCall("Carl says hi", "v1", 0), h.Engine.History[1]);
    }

    [Fact]
    public async Task Nicknames_can_be_switched_off()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.UseNicknames = false;
        h.Users.Update("Twitch", "bob-id", "Bob", u => u.Nickname = "Bobby");

        await h.SayAsync(Message("Bob", "hello"));
        await h.DrainAsync();

        Assert.Equal(["Bob says hello"], h.Spoken);
    }

    [Fact]
    public async Task Ignored_users_are_not_read_but_forced_users_bypass_permissions()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.Permission = ChatAudience.Subscribers;
        h.Users.Update("Twitch", "", "Ignored", u => u.Ignored = true);
        h.Users.Update("Twitch", "", "Forced", u => u.Forced = true);

        await h.SayAsync(Message("Ignored", "a", sub: true));
        await h.SayAsync(Message("Forced", "b"));
        await h.SayAsync(Message("Viewer", "c"));
        await h.SayAsync(Message("Subscriber", "d", sub: true));
        await h.DrainAsync();

        Assert.Equal(["Forced says b", "Subscriber says d"], h.Spoken);
    }

    [Fact]
    public async Task Users_are_persisted_and_reloaded()
    {
        await using var h = await StartAsync();
        h.Service.SetUserVoice("Twitch", "bob-id", "Bob", "Robot");

        h.FlushUsers();
        var store = JsonSerializer.Deserialize<SpeechUserStore>(await File.ReadAllTextAsync(h.UsersFile), Json.Options)!;
        var reloaded = new SpeechUserDirectory(store, () => { });

        var user = reloaded.Find("twitch", "BOB-ID", null)!;
        Assert.Equal(("Bob", "Robot"), (user.UserName, user.Alias));
    }

    [Fact]
    public void Users_match_by_id_first_then_by_name()
    {
        var users = SpeechUserDirectory.InMemory();
        var named = users.GetOrAdd("Twitch", "", "Alice");

        var byChat = users.GetOrAdd("Twitch", "a1", "alice");
        Assert.Same(named, byChat);
        Assert.Equal("a1", byChat.UserId);

        var renamed = users.GetOrAdd("Twitch", "a1", "AliceNew");
        Assert.Same(named, renamed);
        Assert.Equal("AliceNew", renamed.UserName);
        Assert.Null(users.Find("Rumble", "a1", null));
        Assert.True(users.Remove(named));
        Assert.Empty(users.All);
    }

    [Fact]
    public async Task Highlight_alias_and_name_grace_period()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.HighlightAlias = "Robot";
        h.Settings.SpeakChat.NameGraceSeconds = 30;

        await h.SayAsync(Message("Bob", "look at me", extra: new Dictionary<string, object?> { ["isHighlighted"] = true }));
        await h.SayAsync(Message("Bob", "again"));
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.SayAsync(Message("Bob", "later"));
        await h.DrainAsync();

        Assert.Equal(["Bob says look at me", "again", "Bob says later"], h.Spoken);
        Assert.Equal("robot", h.Engine.History[0].VoiceId);
        Assert.Equal("v1", h.Engine.History[1].VoiceId);
    }

    [Fact]
    public async Task Platform_filter_limits_chat_reading()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.Platforms.Add("rumble");

        await h.SayAsync(Message("Bob", "twitch text"));
        await h.SayAsync(Message("Bob", "rumble text", platform: "Rumble"));
        await h.DrainAsync();

        Assert.Equal(["Bob says rumble text"], h.Spoken);
    }

    [Fact]
    public async Task Last_voice_is_remembered_for_assign_last()
    {
        await using var h = await StartAsync();

        await h.SayAsync(Message("Bob", "hello"));
        await h.DrainAsync();

        Assert.Equal("Main|fake|v1", h.Users.Find("Twitch", "bob-id", null)!.LastVoice);
    }
}
