using Cuebot.Speech.Tests.Fakes;
using static Cuebot.Speech.Tests.Fakes.ChatEvents;

namespace Cuebot.Speech.Tests;

public class TtsCommandTests
{
    private static async Task<SpeechHarness> StartAsync()
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%user%: %message%";
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Robot", EngineId = "fake", VoiceId = "robot" });
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Secret", EngineId = "fake", VoiceId = "secret" });
        return h;
    }

    private static Task Mod(SpeechHarness h, string text) => h.SayAsync(Message("Moddy", text, mod: true));
    private static Task Viewer(SpeechHarness h, string text, string user = "Alice") => h.SayAsync(Message(user, text));

    [Fact]
    public async Task Commands_are_not_spoken_and_reply_on_the_message_platform()
    {
        await using var h = await StartAsync();

        await h.SayAsync(Message("Alice", "!tts status", platform: "Rumble"));
        await h.DrainAsync();

        Assert.Empty(h.Spoken);
        var (platform, text) = Assert.Single(h.Chat.Replies);
        Assert.Equal("Rumble", platform);
        Assert.Equal("@Alice TTS is on, 0 queued, mode all, events off.", text);
    }

    [Fact]
    public async Task Prefix_is_configurable_and_must_be_a_whole_word()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.Prefix = "!speak";

        await Viewer(h, "!speak about");
        await Viewer(h, "!tts about");
        await Viewer(h, "!speakers are loud");
        await h.DrainAsync();

        Assert.Single(h.Chat.Replies);
        Assert.Empty(h.Spoken); // "!" messages are ignored by default
    }

    [Fact]
    public async Task Bare_prefix_and_unknown_commands_reply_with_help()
    {
        await using var h = await StartAsync();

        await Viewer(h, "!tts");
        await Viewer(h, "!tts dance");

        Assert.StartsWith("@Alice !tts voices, voice, speed, pitch, nick, me, commands, about, status", h.Chat.Replies[0].Text);
        Assert.DoesNotContain("pause", h.Chat.Replies[0].Text);
        Assert.Equal("@Alice unknown command. Try !tts commands", h.Chat.Replies[1].Text);
    }

    [Fact]
    public async Task Moderator_commands_are_refused_to_viewers()
    {
        await using var h = await StartAsync();

        await Viewer(h, "!tts pause");

        Assert.False(h.Service.Paused);
        Assert.Equal("@Alice, you are not allowed to use that.", h.Chat.Last);
    }

    [Fact]
    public async Task Permissions_can_be_overridden_per_command()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.Permissions["pause"] = ChatAudience.Vips;
        h.Settings.Commands.Permissions["voices"] = ChatAudience.Subscribers;

        await h.SayAsync(Message("Vee", "!tts pause", vip: true));
        await Viewer(h, "!tts voices");

        Assert.True(h.Service.Paused);
        Assert.Equal("@Alice, you are not allowed to use that.", h.Chat.Last);
    }

    [Fact]
    public async Task Broadcaster_may_use_everything()
    {
        await using var h = await StartAsync();

        await h.SayAsync(Message("Streamer", "!tts off", broadcaster: true));

        Assert.False(h.Service.Enabled);
    }

    [Fact]
    public async Task Viewer_commands_have_a_cooldown_moderators_skip()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.ViewerCooldownSeconds = 30;
        h.Settings.Commands.Cooldowns["about"] = 0;

        await Viewer(h, "!tts status");
        await Viewer(h, "!tts status");
        await Viewer(h, "!tts about");
        await Viewer(h, "!tts about");
        await Mod(h, "!tts status");
        await Mod(h, "!tts status");
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await Viewer(h, "!tts status");

        Assert.Equal(6, h.Chat.Replies.Count);
    }

    [Fact]
    public async Task Silence_output_suppresses_replies_but_runs_commands()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.SilenceOutput = true;

        await Mod(h, "!tts pause");

        Assert.True(h.Service.Paused);
        Assert.Empty(h.Chat.Replies);
    }

    [Fact]
    public async Task Voices_lists_viewer_aliases()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.ViewerAliases.AddRange(["Robot", "main"]);

        await Viewer(h, "!tts voices");
        await Mod(h, "!tts voices");

        Assert.Equal("@Alice voices: Main, Robot (1 engines available)", h.Chat.Replies[0].Text);
        Assert.Equal("@Moddy voices: Main, Robot, Secret (1 engines available)", h.Chat.Replies[1].Text);
    }

    [Fact]
    public async Task Viewers_set_show_and_reset_their_voice()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.ViewerAliases.Add("Robot");

        await Viewer(h, "!tts voice");
        await Viewer(h, "!tts voice robot");
        await Viewer(h, "!tts voice");
        await Viewer(h, "hello");
        await h.DrainAsync();
        await Viewer(h, "!tts voice set Secret");
        await Viewer(h, "!tts voice reset");

        Assert.Equal(
        [
            "@Alice you have no voice set.",
            "@Alice your voice is now Robot.",
            "@Alice your voice is Robot.",
            "@Alice unknown voice Secret.",
            "@Alice your voice was reset.",
        ], h.Chat.Replies.Select(r => r.Text));
        Assert.Equal("robot", h.Engine.History.First(c => c.Text == "Alice: hello").VoiceId);
        Assert.Equal("", h.Users.Find("Twitch", "alice-id", null)!.Alias);
    }

    [Fact]
    public async Task Speed_and_pitch_override_the_alias()
    {
        await using var h = await StartAsync();

        await Viewer(h, "!tts speed 4");
        await Viewer(h, "!tts pitch -3");
        await Viewer(h, "!tts speed 11");
        await Viewer(h, "!tts rate fast");
        await Viewer(h, "hi");
        await h.DrainAsync();
        await Viewer(h, "!tts pitch reset");

        var call = h.Engine.History.First(c => c.Text == "Alice: hi");
        Assert.Equal((4, -3), (call.Rate, call.Pitch));
        Assert.Equal("@Alice usage: !tts speed <-10..10|reset>", h.Chat.Replies[2].Text);
        Assert.Equal("@Alice usage: !tts speed <-10..10|reset>", h.Chat.Replies[3].Text);
        Assert.Equal("@Alice your pitch was reset.", h.Chat.Last);
        Assert.Null(h.Users.Find("Twitch", "alice-id", null)!.Pitch);
    }

    [Fact]
    public async Task Viewers_set_their_nickname_unless_it_is_bad_or_disallowed()
    {
        await using var h = await StartAsync();
        h.Settings.Filters.BadWords.Add("darn");

        await Viewer(h, "!tts nick Ally Cat");
        await Viewer(h, "hi");
        await h.DrainAsync();
        await Viewer(h, "!tts nick darn it");
        await Viewer(h, "!tts nick " + new string('x', 31));
        h.Settings.Commands.AllowViewerNicknames = false;
        await Viewer(h, "!tts nick Bob");

        Assert.Contains("Ally Cat: hi", h.Spoken);
        Assert.Equal("@Alice that nickname is not allowed.", h.Chat.Replies[1].Text);
        Assert.Contains("at most 30", h.Chat.Replies[2].Text);
        Assert.Equal("@Alice nicknames are set by moderators.", h.Chat.Last);
    }

    [Fact]
    public async Task Me_off_opts_a_viewer_out_and_on_back_in()
    {
        await using var h = await StartAsync();

        await Viewer(h, "!tts me off");
        await Viewer(h, "quiet please");
        await Viewer(h, "!tts self on");
        await Viewer(h, "loud again");
        await h.DrainAsync();

        Assert.Equal(["Alice: loud again"], h.Spoken);
    }

    [Fact]
    public async Task Pause_resume_clear_and_skip()
    {
        await using var h = await StartAsync();
        h.Player.Block = true;

        await Viewer(h, "one");
        await h.Player.NextAsync();
        await Mod(h, "!tts pause");
        await Viewer(h, "two");
        await Viewer(h, "three");
        Assert.Equal(2, h.Service.QueueLength);
        await Mod(h, "!tts clear");
        Assert.Equal(0, h.Service.QueueLength);
        await Mod(h, "!tts resume");
        h.Player.Block = false;
        await Mod(h, "!tts stop"); // ends the blocked "one"
        await h.DrainAsync();

        Assert.Equal(["@Moddy TTS paused.", "@Moddy cleared 2 queued message(s).", "@Moddy TTS resumed."],
            h.Chat.Replies.Select(r => r.Text));
        Assert.Equal(["Alice: one"], h.Spoken);
    }

    [Fact]
    public async Task Skip_user_drops_their_queued_messages()
    {
        await using var h = await StartAsync();
        h.Service.Paused = true;

        await Viewer(h, "spam 1", "Spammer");
        await Viewer(h, "spam 2", "Spammer");
        await Viewer(h, "fine", "Bob");
        await Mod(h, "!tts skip @spammer");
        h.Service.Paused = false;
        await h.DrainAsync();

        Assert.Equal(["Bob: fine"], h.Spoken);
        Assert.Equal("@Moddy skipped 2 message(s) from spammer.", h.Chat.Last);
    }

    [Fact]
    public async Task Off_stops_and_ignores_until_on()
    {
        await using var h = await StartAsync();

        await Mod(h, "!tts disable");
        await Viewer(h, "ignored");
        await Mod(h, "!tts enable");
        await Viewer(h, "spoken");
        await h.DrainAsync();

        Assert.Equal(["Alice: spoken"], h.Spoken);
        Assert.True(h.Settings.Enabled);
    }

    [Fact]
    public async Task Mode_command_only_speaks_speaking_commands()
    {
        await using var h = await StartAsync();

        await Mod(h, "!tts mode command");
        await Viewer(h, "not spoken");
        await Viewer(h, "!say spoken");
        await Mod(h, "!tts mode all");
        await Viewer(h, "spoken too");
        await Mod(h, "!tts mode sometimes");
        await h.DrainAsync();

        Assert.Equal(["Alice: spoken", "Alice: spoken too"], h.Spoken);
        Assert.Equal("@Moddy usage: !tts mode all|command", h.Chat.Last);
    }

    [Fact]
    public async Task Mode_command_needs_a_speaking_command()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.SpeakCommands.Clear();

        await Mod(h, "!tts mode command");

        Assert.Equal(SpeakMode.All, h.Settings.SpeakChat.Mode);
        Assert.Contains("needs at least one", h.Chat.Last);
    }

    [Fact]
    public async Task Ignore_add_del_and_list()
    {
        await using var h = await StartAsync();

        await Mod(h, "!tts ignore add @Alice");
        await Viewer(h, "hidden");
        await Mod(h, "!tts ignored");
        await Mod(h, "!tts ignore del alice");
        await Viewer(h, "visible");
        await Mod(h, "!tts ignore maybe alice");
        await h.DrainAsync();

        Assert.Equal(["Alice: visible"], h.Spoken);
        Assert.Equal("@Moddy ignored: Alice", h.Chat.Replies[1].Text);
        Assert.Equal("@Moddy usage: !tts ignore add|del <user>", h.Chat.Last);
    }

    [Fact]
    public async Task Regulars_count_for_permissions()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.Permission = ChatAudience.Regulars;

        await Viewer(h, "before");
        await Mod(h, "!tts reg add alice");
        await Viewer(h, "after");
        await Mod(h, "!tts reg del alice");
        await Viewer(h, "removed");
        await h.DrainAsync();

        Assert.Equal(["Alice: after"], h.Spoken);
    }

    [Fact]
    public async Task Events_on_off()
    {
        await using var h = await StartAsync();

        await Mod(h, "!tts events on");
        Assert.True(h.Service.EventsEnabled);
        await Mod(h, "!tts events off");
        Assert.False(h.Service.EventsEnabled);
        await Mod(h, "!tts events");
        Assert.Contains("usage", h.Chat.Last);
    }

    [Fact]
    public async Task Set_nickname_sticky_and_voice()
    {
        await using var h = await StartAsync();

        await Mod(h, "!tts set nickname Alice Al");
        await Viewer(h, "hey");
        await h.DrainAsync();
        await Mod(h, "!tts set nickname Alice");
        await Mod(h, "!tts set sticky off");
        await Mod(h, "!tts set voice Alice Secret");
        await Viewer(h, "secretly");
        await h.DrainAsync();

        Assert.Equal("Al: hey", h.Spoken[0]);
        Assert.Equal("@Moddy removed Alice's nickname.", h.Chat.Replies[1].Text);
        Assert.False(h.Settings.SpeakChat.StickyRandom);
        Assert.Equal("secret", h.Engine.History.First(c => c.Text == "Alice: secretly").VoiceId);
    }

    [Fact]
    public async Task Random_reset_forgets_the_sticky_voice()
    {
        await using var h = await StartAsync();
        h.Users.Update("Twitch", "alice-id", "Alice", u => u.RandomVoice = "fake|old");

        await Mod(h, "!tts random reset alice");

        Assert.Equal("", h.Users.Find("Twitch", "alice-id", "Alice")!.RandomVoice);
    }

    [Fact]
    public async Task Assign_last_pins_the_heard_random_voice_in_a_new_alias()
    {
        await using var h = await StartAsync();
        h.Settings.SpeakChat.RandomVoices = true;
        h.Settings.Aliases.Clear();
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Main", EngineId = "fake", VoiceId = "v1" });

        await Viewer(h, "random me");
        await h.DrainAsync();
        var heard = h.Engine.History.First(c => c.Text == "Alice: random me").VoiceId;
        await Mod(h, "!tts assign last alice");

        var user = h.Users.Find("Twitch", "alice-id", null)!;
        Assert.Equal("Alice voice", user.Alias);
        Assert.Equal(heard, h.Settings.Aliases.Single(a => a.Name == "Alice voice").VoiceId);
        Assert.Equal("@Moddy Alice now always speaks with Alice voice.", h.Chat.Last);
    }

    [Fact]
    public async Task Assign_last_reuses_a_single_voice_alias_and_assign_names_one()
    {
        await using var h = await StartAsync();

        await Viewer(h, "hello");
        await h.DrainAsync();
        await Mod(h, "!tts assign last Alice");
        Assert.Equal("Main", h.Users.Find("Twitch", null, "alice")!.Alias);

        await Mod(h, "!tts assign Alice robot");
        Assert.Equal("Robot", h.Users.Find("Twitch", null, "alice")!.Alias);

        await Mod(h, "!tts assign last Nobody");
        Assert.Equal("@Moddy Nobody has not spoken yet.", h.Chat.Last);
    }

    [Fact]
    public async Task Profile_lists_and_activates_voice_ignore_profiles()
    {
        await using var h = await StartAsync();
        h.Settings.VoiceIgnoreProfiles.Add(new VoiceIgnoreProfile { Name = "No German", Locales = ["de"] });

        await Mod(h, "!tts profile list");
        await Mod(h, "!tts profile activate no german");
        await Mod(h, "!tts profile bogus");
        await Mod(h, "!tts profile none");

        Assert.Equal("@Moddy ignore profiles: No German (active: none).", h.Chat.Replies[0].Text);
        Assert.Equal("@Moddy ignore profile: No German.", h.Chat.Replies[1].Text);
        Assert.Equal("@Moddy no ignore profile named bogus.", h.Chat.Replies[2].Text);
        Assert.Equal("", h.Settings.ActiveVoiceIgnoreProfile);
    }

    [Fact]
    public async Task About_and_aboot()
    {
        await using var h = await StartAsync();

        await Viewer(h, "!tts about");
        await Viewer(h, "!tts aboot");

        Assert.All(h.Chat.Replies, r => Assert.StartsWith("@Alice Cuebot text-to-speech", r.Text));
    }

    [Fact]
    public async Task Commands_work_while_speech_is_disabled()
    {
        await using var h = await StartAsync();
        h.Service.Enabled = false;

        await Mod(h, "!tts on");

        Assert.True(h.Service.Enabled);
    }

    [Fact]
    public async Task Custom_speaking_commands_use_their_alias_and_permission()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.CustomCommands.Add(new TtsCustomCommand { Command = "!robot", Alias = "Robot", Permission = ChatAudience.Subscribers });

        await Viewer(h, "!robot beep boop");
        await h.SayAsync(Message("Subby", "!robot beep boop", sub: true));
        await h.DrainAsync();

        Assert.Equal(["Subby: beep boop"], h.Spoken);
        Assert.Equal("robot", h.Engine.History[0].VoiceId);
        Assert.Equal("@Alice, you are not allowed to use that.", h.Chat.Last);
    }

    [Fact]
    public async Task Disabled_commands_are_read_like_any_message()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.Enabled = false;
        h.Settings.Filters.IgnoredPrefixes = [];

        await Viewer(h, "!tts pause");
        await h.DrainAsync();

        Assert.False(h.Service.Paused);
        Assert.Equal(["Alice: !tts pause"], h.Spoken);
    }

    [Fact]
    public async Task VoiceGate_command_switches_profiles()
    {
        await using var h = await StartAsync();
        h.Settings.VoiceGate.Profiles.Add(new VoiceGateProfile { Name = "Mic" });

        await Mod(h, "!tts voicegate mic");
        Assert.Equal("Mic", h.Settings.VoiceGate.ActiveProfile);
        await Mod(h, "!tts voicegate none");
        Assert.Equal("", h.Settings.VoiceGate.ActiveProfile);
    }
}
