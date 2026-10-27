using Cuebot.Speech.Tests.Fakes;
using static Cuebot.Speech.Tests.Fakes.ChatEvents;

namespace Cuebot.Speech.Tests;

public class FilterTests
{
    private static readonly SpeakChatSettings Legacy = new() { MaxLength = 0, SkipLinks = false, IgnoreCommands = false };

    private static FilterProfile Plain() => new() { MaxRepeatedCharacters = 0, MaxRepeatedWords = 0, IgnoreBots = false };

    private static string? Apply(string text, FilterProfile profile, string ranges = "", params string[] emotes) =>
        MessageFilter.Apply(text, profile, Legacy, ranges, emotes.ToHashSet()).Text;

    [Theory]
    [InlineData(BadWordAction.Disabled, "you darn fool")]
    [InlineData(BadWordAction.Strip, "you fool")]
    [InlineData(BadWordAction.Replace, "you beep fool")]
    [InlineData(BadWordAction.Skip, null)]
    public void Bad_word_actions(BadWordAction action, string? expected)
    {
        var profile = Plain();
        profile.BadWords.Add("DARN");
        profile.BadWordAction = action;

        Assert.Equal(expected, Apply("you darn fool", profile));
    }

    [Fact]
    public void Bad_words_match_whole_words_and_phrases_only()
    {
        Assert.Equal("darned is fine", TextFilter.ApplyBadWords("darned is fine", ["darn"], BadWordAction.Strip));
        Assert.Equal("say  please", TextFilter.ApplyBadWords("say heck no please", ["heck no"], BadWordAction.Strip));
        Assert.Equal("a ** b", TextFilter.ApplyBadWords("a c++ b", ["c++"], BadWordAction.Replace, "**"));
    }

    [Theory]
    [InlineData(UrlMode.Keep, "see example.com now")]
    [InlineData(UrlMode.Replace, "see link now")]
    [InlineData(UrlMode.Strip, "see now")]
    [InlineData(UrlMode.Skip, null)]
    public void Url_modes(UrlMode mode, string? expected)
    {
        var profile = Plain();
        profile.UrlMode = mode;

        Assert.Equal(expected, Apply("see example.com now", profile));
    }

    [Fact]
    public void Url_mode_falls_back_to_the_legacy_switch_and_custom_word()
    {
        var profile = Plain();
        profile.UrlWord = "a website";

        Assert.Equal("go a website", MessageFilter.Apply("go www.x.tv", profile, new SpeakChatSettings()).Text);
        Assert.Equal("go www.x.tv", MessageFilter.Apply("go www.x.tv", profile, Legacy).Text);
    }

    [Fact]
    public void Max_words_and_characters()
    {
        var profile = Plain();
        profile.MaxWords = 3;
        Assert.Equal("one two three", Apply("one two three four five", profile));

        profile.MaxWords = 0;
        profile.MaxCharacters = 7;
        Assert.Equal("one two", Apply("one two three", profile));
    }

    [Fact]
    public void Max_characters_falls_back_to_legacy_max_length() =>
        Assert.Equal("abc", MessageFilter.Apply("abcdef", Plain(), new SpeakChatSettings { MaxLength = 3 }).Text);

    [Theory]
    [InlineData("noooooooo", 3, "nooo")]
    [InlineData("!!!!!!", 2, "!!")]
    [InlineData("1000000 viewers", 2, "1000000 viewers")]
    [InlineData("aaa", 0, "aaa")]
    public void Repeated_characters_are_compressed_but_numbers_kept(string input, int max, string expected) =>
        Assert.Equal(expected, TextFilter.CompressRepeatedCharacters(input, max));

    [Fact]
    public void Repeated_words_are_compressed_case_insensitively() =>
        Assert.Equal("LUL lul hi KEKW", TextFilter.CompressRepeatedWords("LUL lul LUL LUL hi KEKW", 2));

    [Fact]
    public void Twitch_emote_ranges_are_stripped_by_code_point()
    {
        // "😀 Kappa hi Kappa PogChamp": the emoji is one code point but two UTF-16 chars.
        const string text = "😀 Kappa hi Kappa PogChamp";
        const string ranges = "25:2-6,11-15/88:17-24";
        var profile = Plain();

        Assert.Equal("😀 hi", Apply(text, profile, ranges));

        profile.AllowFirstEmote = true;
        Assert.Equal("😀 Kappa hi PogChamp", Apply(text, profile, ranges));

        profile.AllowFirstEmote = false;
        profile.AllowedEmotes.Add("PogChamp");
        Assert.Equal("😀 hi PogChamp", Apply(text, profile, ranges));

        profile.StripEmotes = false;
        Assert.Equal(text, Apply(text, profile, ranges));
    }

    [Fact]
    public void Malformed_emote_ranges_are_ignored() =>
        Assert.Equal("hi there", TextFilter.StripEmoteRanges("hi there", "x:a-b,9-2,0-99/nocolon"));

    [Fact]
    public void Third_party_emote_names_are_stripped_case_sensitively() =>
        Assert.Equal("that is catJAM", Apply("that OMEGALUL is catJAM", Plain(), "", "OMEGALUL", "catjam"));

    [Fact]
    public void Emoji_can_be_stripped()
    {
        var profile = Plain();
        profile.StripEmoji = true;

        Assert.Equal("hi there", Apply("hi 👋🏽 there ❤️ 👨‍👩‍👧", profile));
    }

    [Fact]
    public void Ignore_rules()
    {
        var profile = new FilterProfile { IgnoredPrefixes = ["!", "#"], BotNames = ["mybot"] };
        profile.IgnoredUsers.Add("@Troll");
        ChatContext Chat(string user, string message, bool shared = false) =>
            new("Twitch", user, user + "-id", message) { IsSharedChat = shared };

        Assert.Equal(SkipReason.IgnoredUser, MessageFilter.CheckIgnore(Chat("troll", "hi"), profile, Legacy));
        Assert.Equal(SkipReason.Bot, MessageFilter.CheckIgnore(Chat("Nightbot", "hi"), profile, Legacy));
        Assert.Equal(SkipReason.Bot, MessageFilter.CheckIgnore(Chat("MyBot", "hi"), profile, Legacy));
        Assert.Equal(SkipReason.IgnoredPrefix, MessageFilter.CheckIgnore(Chat("bob", "  #tag"), profile, Legacy));
        Assert.Equal(SkipReason.None, MessageFilter.CheckIgnore(Chat("bob", "hi", shared: true), profile, Legacy));
        profile.IgnoreSharedChat = true;
        Assert.Equal(SkipReason.SharedChat, MessageFilter.CheckIgnore(Chat("bob", "hi", shared: true), profile, Legacy));
        profile.IgnoreBots = false;
        Assert.Equal(SkipReason.None, MessageFilter.CheckIgnore(Chat("Nightbot", "hi"), profile, Legacy));
    }

    [Theory]
    [InlineData(false, "", "", false)]
    [InlineData(true, "", "", true)]
    [InlineData(false, "111", "111", false)]
    [InlineData(false, "222", "111", true)]
    [InlineData(false, "222", "", false)]
    public void Shared_chat_detection(bool flagged, string source, string channel, bool expected) =>
        Assert.Equal(expected, ChatContext.IsShared(flagged, source, channel));

    [Fact]
    public void Named_profiles_resolve_with_default_fallback()
    {
        var settings = new SpeechSettings();
        var strict = new FilterProfile { Name = "Strict" };
        settings.FilterProfiles.Add(strict);

        Assert.Same(strict, MessageFilter.Resolve(settings, "strict"));
        Assert.Same(settings.Filters, MessageFilter.Resolve(settings, ""));
        Assert.Same(settings.Filters, MessageFilter.Resolve(settings, "missing"));
    }

    [Fact]
    public async Task Chat_uses_its_selected_profile_and_replacements_run_last()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%message%";
        h.Settings.FilterProfiles.Add(new FilterProfile { Name = "Chat", BadWords = ["heck"], BadWordAction = BadWordAction.Replace, BadWordReplacement = "HECK" });
        h.Settings.SpeakChat.FilterProfile = "Chat";
        h.Settings.Replacements.Add(new WordReplacement { Find = "HECK", Replace = "gosh" });
        h.Settings.Replacements.Add(new WordReplacement { Find = @"\d{3,}", Replace = "", IsRegex = true });

        await h.SayAsync(Message("Bob", "what the heck 12345"));
        await h.DrainAsync();

        Assert.Equal(["what the gosh"], h.Spoken);
    }

    [Fact]
    public async Task Chat_strips_platform_and_provider_emotes_and_ignores_shared_chat()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%message%";
        h.Settings.Filters.IgnoreSharedChat = true;
        h.Emotes.Names.Add("catJAM");

        await h.SayAsync(Message("Bob", "Kappa hello catJAM", extra: new Dictionary<string, object?> { ["emotes"] = "25:0-4" }));
        await h.SayAsync(Message("Guest", "from elsewhere", extra: new Dictionary<string, object?>
        {
            ["sourceBroadcasterId"] = "999",
            ["broadcasterId"] = "123",
        }));
        await h.SayAsync(Message("Carl", "Kappa Kappa", extra: new Dictionary<string, object?> { ["emotes"] = "25:0-4,6-10" }));
        await h.DrainAsync();

        Assert.Equal(["hello"], h.Spoken);
    }

    [Fact]
    public async Task Speak_with_filters_applies_a_profile_to_action_text()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Filters.BadWords.Add("bad");
        h.Settings.Filters.BadWordAction = BadWordAction.Skip;

        var filtered = await h.Service.SpeakAsync("a bad text", new SpeakOptions { ApplyFilters = true }, wait: true);
        await h.Service.SpeakAsync("a bad text", new SpeakOptions(), wait: true);

        Assert.Equal(SpeechResult.None, filtered);
        Assert.Equal(["a bad text"], h.Spoken);
    }
}
