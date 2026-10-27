using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Speech;
using Cuebot.Speech;

namespace Cuebot.UiTests.Speech;

/// <summary>Speech tabs write straight through to speech.json, and secrets stay out of strings and logs.</summary>
public sealed class SpeechViewModelTests
{
    private const string Secret = "sk-TOP-SECRET-0123456789";

    [AvaloniaFact]
    public async Task Alias_voices_migrate_from_the_single_voice_form_and_persist()
    {
        await using var h = new SpeechHarness(s => s.Aliases.Add(new VoiceAlias { Name = "Host", EngineId = "fake", VoiceId = "v1", Volume = 80 }));
        var aliases = h.ViewModel.Aliases;
        var editor = Assert.IsType<VoiceAliasEditorViewModel>(aliases.Selected);
        var first = Assert.Single(editor.Voices);
        Assert.Equal("Alice", first.SelectedVoice?.Name);
        Assert.Empty(h.Speech.Settings.Aliases[0].Voices); // nothing changes until edited

        editor.AddVoiceCommand.Execute(null);
        var second = editor.Voices[1];
        second.SelectedVoice = second.VoiceOptions.Single(v => v.Id == "v2");
        second.Weight = 3;
        editor.Selection = VoiceSelection.RoundRobin;
        editor.Style = editor.Styles.Single(s => s.Value == "cheerful");
        editor.Pitch = 4;
        editor.Device = editor.Devices.Single(d => d.Value == "vcable");
        editor.Name = "Host 2";

        var saved = Assert.Single(h.Reload().Aliases);
        Assert.Equal("Host 2", saved.Name);
        Assert.Equal(["v1", "v2"], saved.Voices.Select(v => v.VoiceId));
        Assert.Equal(3, saved.Voices[1].Weight);
        Assert.Equal(VoiceSelection.RoundRobin, saved.Selection);
        Assert.Equal("cheerful", saved.Style);
        Assert.Equal(4, saved.Pitch);
        Assert.Equal(80, saved.Volume);
        Assert.Equal("vcable", saved.DeviceId);
        Assert.Equal(("fake", "v1"), (saved.EngineId, saved.VoiceId)); // single-voice fields mirror the first voice
        Assert.Equal(1, h.Local.VoiceCalls); // the catalog lists each engine's voices once

        editor.RemoveVoiceCommand.Execute(editor.Voices[0]);
        Assert.Equal(["v2"], h.Reload().Aliases[0].Voices.Select(v => v.VoiceId));
        Assert.False(editor.Voices[0].CanRemove); // the last voice stays
    }

    [AvaloniaFact]
    public async Task Renaming_an_alias_updates_references_and_pickers()
    {
        await using var h = new SpeechHarness(s =>
        {
            s.Aliases.Add(new VoiceAlias { Name = "Host", EngineId = "fake", VoiceId = "v1" });
            s.DefaultAlias = "Host";
            s.SpeakChat.Alias = "Host";
        });
        h.ViewModel.Aliases.Selected!.Name = "Anchor";
        Assert.Equal("Anchor", h.ViewModel.Aliases.DefaultAlias?.Value);
        Assert.Contains(h.ViewModel.Chat.AliasChoices, c => c.Value == "Anchor");
        Assert.Equal("Anchor", h.ViewModel.Chat.Alias?.Value);
        var saved = h.Reload();
        Assert.Equal("Anchor", saved.DefaultAlias);
        Assert.Equal("Anchor", saved.SpeakChat.Alias);
    }

    [AvaloniaFact]
    public async Task Filter_profile_edits_persist_and_follow_renames()
    {
        await using var h = new SpeechHarness();
        var filters = h.ViewModel.Filters;
        Assert.True(filters.Profiles[0].IsDefault);
        Assert.False(filters.RemoveProfileCommand.CanExecute(null));

        filters.AddProfileCommand.Execute(null);
        var profile = filters.SelectedProfile!;
        profile.Name = "Strict";
        profile.BadWords = "heck\ndarn, gosh";
        profile.BadWordAction = BadWordAction.Replace;
        profile.BadWordReplacement = "bleep";
        profile.UrlMode = UrlMode.Strip;
        profile.MaxWords = 25;
        profile.IgnoredPrefixes = "!, #";
        profile.StripEmoji = true;
        profile.IgnoreSharedChat = true;
        filters.ChatProfile = filters.FilterProfileChoices.Single(c => c.Value == "Strict");
        profile.Name = "Strict 2";
        filters.Profiles[0].MaxRepeatedCharacters = 5;
        filters.AddReplacementCommand.Execute(null);
        filters.Replacements[0].Find = "(";
        filters.Replacements[0].IsRegex = true;
        Assert.True(filters.Replacements[0].HasError);
        filters.AddIgnoreProfileCommand.Execute(null);
        filters.SelectedIgnoreProfile!.Locales = "de, en-GB";
        filters.ActiveIgnoreProfile = filters.IgnoreChoices[1];

        var saved = h.Reload();
        var stored = Assert.Single(saved.FilterProfiles);
        Assert.Equal("Strict 2", stored.Name);
        Assert.Equal(["heck", "darn", "gosh"], stored.BadWords);
        Assert.Equal(BadWordAction.Replace, stored.BadWordAction);
        Assert.Equal("bleep", stored.BadWordReplacement);
        Assert.Equal(UrlMode.Strip, stored.UrlMode);
        Assert.Equal(25, stored.MaxWords);
        Assert.Equal(["!", "#"], stored.IgnoredPrefixes);
        Assert.True(stored.StripEmoji && stored.IgnoreSharedChat);
        Assert.Equal("Strict 2", saved.SpeakChat.FilterProfile);
        Assert.Equal(5, saved.Filters.MaxRepeatedCharacters);
        Assert.True(Assert.Single(saved.Replacements).IsRegex);
        Assert.Equal(["de", "en-GB"], Assert.Single(saved.VoiceIgnoreProfiles).Locales);
        Assert.Equal(saved.VoiceIgnoreProfiles[0].Name, saved.ActiveVoiceIgnoreProfile);

        filters.RemoveProfileCommand.Execute(null);
        Assert.Equal("", h.Reload().SpeakChat.FilterProfile); // chat falls back to the default profile
    }

    [AvaloniaFact]
    public async Task Engine_credentials_are_stored_but_never_exposed()
    {
        await using var h = new SpeechHarness();
        var card = h.ViewModel.Engines.Cards.Single(c => c.Id == "azure");
        Assert.False(card.IsAvailable);
        var key = card.Fields.Single(f => f.IsSecret);
        Assert.Equal('•', key.PasswordChar);

        key.Value = $"  {Secret}  ";
        card.Fields.Single(f => f.Label == "Region").Value = "westeurope";
        Assert.Equal(Secret, h.Speech.Settings.Engines.Azure.Key);
        Assert.True(card.IsAvailable);
        await card.TestCommand.ExecuteAsync(null);
        Assert.Equal("1 voice", card.TestResult);

        var saved = h.Reload();
        Assert.Equal(Secret, saved.Engines.Azure.Key);
        Assert.Equal("westeurope", saved.Engines.Azure.Region);

        foreach (var text in new[] { key.ToString(), card.ToString(), h.ViewModel.ToString(), h.ViewModel.Engines.ToString()! })
            Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(h.Log.Recent(), e => e.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, card.Redact($"401 for key {Secret}"));

        // Rendered: the key sits only in a masked text box, never in a text block or tooltip.
        var window = SpeechWindow.Show(h.ViewModel);
        h.ViewModel.SelectedTab = 1;
        UiPump.Pump();
        var visuals = window.GetVisualDescendants().ToArray();
        Assert.DoesNotContain(visuals.OfType<TextBlock>(), t => t.Text?.Contains(Secret, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(visuals.OfType<Control>(), c => ToolTip.GetTip(c)?.ToString()?.Contains(Secret, StringComparison.Ordinal) == true);
        var box = Assert.Single(visuals.OfType<TextBox>(), t => t.Text == Secret);
        Assert.Equal('•', box.PasswordChar);
        Assert.False(box.RevealPassword);
        key.Revealed = true;
        UiPump.Pump();
        Assert.True(box.RevealPassword);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Chat_commands_and_events_write_overrides_only_when_changed()
    {
        await using var h = new SpeechHarness(s => s.Aliases.Add(new VoiceAlias { Name = "Host", EngineId = "fake", VoiceId = "v1" }));
        var chat = h.ViewModel.Chat;
        chat.SpeakChatEnabled = true;
        chat.Mode = SpeakMode.Command;
        chat.Permission.Everyone = false;
        chat.Permission.Subscribers = true;
        chat.Platforms = "Twitch, Rumble";
        chat.Prefix = "!speak";

        var voice = chat.SubCommands.Single(c => c.Name == "voice");
        voice.Audience.Everyone = false;
        voice.Audience.Vips = true;
        voice.Cooldown = 20;
        var pause = chat.SubCommands.Single(c => c.Name == "pause");
        pause.Enabled = false;
        chat.SubCommands.Single(c => c.Name == "skip").Audience.Moderators = true; // already the default

        chat.AddCustomCommandCommand.Execute(null);
        chat.CustomCommands[0].Text = "!pirate";
        chat.CustomCommands[0].Alias = chat.CommandAliasChoices.Single(c => c.Value == "Host");

        var raid = h.ViewModel.Events.Kinds.Single(k => k.Kind == "Raid");
        Assert.False(raid.IsCustomized);
        raid.Messages[0].Weight = 2;
        raid.AddMessageCommand.Execute(null);
        raid.Messages[1].Text = "Raid!";
        h.ViewModel.Events.MinRaidViewers = 5;

        var saved = h.Reload();
        Assert.True(saved.SpeakChat.Enabled);
        Assert.Equal(SpeakMode.Command, saved.SpeakChat.Mode);
        Assert.Equal(ChatAudience.Subscribers, saved.SpeakChat.Permission);
        Assert.Equal(["Twitch", "Rumble"], saved.SpeakChat.Platforms);
        Assert.Equal("!speak", saved.Commands.Prefix);
        Assert.Equal(ChatAudience.Vips, saved.Commands.Permissions["voice"]);
        Assert.Equal(20, saved.Commands.Cooldowns["voice"]);
        Assert.Equal(ChatAudience.None, saved.Commands.Permissions["pause"]);
        Assert.False(saved.Commands.Permissions.ContainsKey("skip"));
        Assert.Equal(("!pirate", "Host"), (saved.Commands.CustomCommands[0].Command, saved.Commands.CustomCommands[0].Alias));
        var template = Assert.Single(saved.Events.Templates);
        Assert.Equal("Raid", template.Kind);
        Assert.Equal([2, 1], template.Messages.Select(m => m.Weight));
        Assert.Equal("Raid!", template.Messages[1].Text);
        Assert.Equal(5, saved.Events.MinRaidViewers);

        voice.ResetCommand.Execute(null);
        raid.ResetToDefaultCommand.Execute(null);
        saved = h.Reload();
        Assert.False(saved.Commands.Permissions.ContainsKey("voice"));
        Assert.Empty(saved.Events.Templates);
    }

    [AvaloniaFact]
    public async Task Users_voicegate_and_overview_write_through()
    {
        await using var h = new SpeechHarness();
        h.Speech.Users.Update("Twitch", "1", "alice", _ => { });
        h.Speech.Users.Update("Twitch", "2", "bob", _ => { });
        var users = h.ViewModel.Users;
        users.Refresh();
        users.Search = "ali";
        var alice = Assert.Single(users.Users);
        alice.Nickname = "Ally";
        alice.Rate = 3;
        alice.Forced = true;
        Assert.Equal("Ally", h.Speech.Users.Find("Twitch", "1", null)!.Nickname);
        Assert.Equal(3, h.Speech.Users.Find("Twitch", "1", null)!.Rate);
        alice.Rate = null;
        Assert.Null(h.Speech.Users.Find("Twitch", "1", null)!.Rate);
        users.Selected = alice;
        users.RemoveCommand.Execute(null);
        Assert.Null(h.Speech.Users.Find("Twitch", "1", "alice"));

        var gate = h.ViewModel.Gate;
        gate.AddCommand.Execute(null);
        gate.Selected!.Device = gate.InputDevices.Single(d => d.Value == "headset");
        gate.Selected.ThresholdDb = -42;
        gate.ActiveProfile = gate.ActiveChoices[1];
        gate.IsMonitoring = true;
        UiPump.Pump();
        Assert.Equal(-20, gate.LevelDb);
        Assert.True(gate.IsAboveThreshold);
        gate.IsMonitoring = false;
        gate.MaxQueueLength = 10;
        gate.DropPolicy = QueueDropPolicy.DropOldest;

        h.ViewModel.Overview.MasterVolume = 55;
        h.ViewModel.Overview.OutputDevice = h.ViewModel.Overview.OutputDevices.Single(d => d.Value == "vcable");
        h.ViewModel.Overview.Paused = true;
        await h.ViewModel.Overview.SpeakCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.Single(h.ViewModel.Overview.Queue);
        h.ViewModel.Overview.ClearQueueCommand.Execute(null);
        UiPump.Pump();
        Assert.Empty(h.ViewModel.Overview.Queue);

        var saved = h.Reload();
        var profile = Assert.Single(saved.VoiceGate.Profiles);
        Assert.Equal(("headset", -42.0), (profile.DeviceId, profile.ThresholdDb));
        Assert.Equal(profile.Name, saved.VoiceGate.ActiveProfile);
        Assert.Equal(10, saved.Queue.MaxLength);
        Assert.Equal(QueueDropPolicy.DropOldest, saved.Queue.DropPolicy);
        Assert.Equal(55, saved.MasterVolume);
        Assert.Equal("vcable", saved.OutputDeviceId);
    }
}
