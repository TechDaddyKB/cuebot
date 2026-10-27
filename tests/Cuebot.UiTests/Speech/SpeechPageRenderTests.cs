using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.Core;
using Cuebot.Speech;

namespace Cuebot.UiTests.Speech;

/// <summary>Every Speech tab renders in both themes with the real composition root; screenshots go to screenshots/.</summary>
public sealed class SpeechPageRenderTests : IAsyncDisposable
{
    private static readonly string[] TabNames = ["overview", "engines", "aliases", "users", "chat", "filters", "events", "voicegate"];

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-speechpage-").FullName;
    private readonly AppHost _host;

    public SpeechPageRenderTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Seed(_host.Get<SpeechService>());
    }

    private static void Seed(SpeechService speech)
    {
        var s = speech.Settings;
        s.Aliases.Add(new VoiceAlias
        {
            Name = "Narrator",
            EngineId = "azure",
            VoiceId = "en-US-JennyNeural",
            Voices =
            [
                new AliasVoice { EngineId = "azure", VoiceId = "en-US-JennyNeural", Weight = 2 },
                new AliasVoice { EngineId = "polly", VoiceId = "Joanna", Weight = 1 },
            ],
        });
        s.Aliases.Add(new VoiceAlias { Name = "Robot", EngineId = "espeak", VoiceId = "en", Rate = 3 });
        s.FilterProfiles.Add(new FilterProfile { Name = "Strict", BadWords = ["heck"], BadWordAction = BadWordAction.Replace });
        s.Replacements.Add(new WordReplacement { Find = "lol", Replace = "laughing out loud" });
        s.Replacements.Add(new WordReplacement { Find = "(", Replace = "", IsRegex = true });
        s.VoiceIgnoreProfiles.Add(new VoiceIgnoreProfile { Name = "No German", Locales = ["de"] });
        s.VoiceGate.Profiles.Add(new VoiceGateProfile { Name = "Desk mic" });
        s.Commands.CustomCommands.Add(new TtsCustomCommand { Command = "!pirate", Alias = "Robot", Permission = ChatAudience.Subscribers });
        s.Commands.Permissions["voice"] = ChatAudience.Subscribers | ChatAudience.Vips;
        s.Commands.Cooldowns["voice"] = 30;
        s.Engines.Azure.Region = "eastus";
        s.Events.Templates.Add(new EventTemplate
        {
            Kind = "Raid", Alias = "Narrator",
            Messages = [new EventMessage { Text = "%name% raids with %viewers%!", Weight = 3 }, new EventMessage { Text = "Raid incoming!" }],
        });
        speech.Users.Update("Twitch", "1", "alice", u => { u.Nickname = "Ali"; u.Regular = true; u.Rate = 2; });
        speech.Users.Update("Rumble", "", "bob_the_builder", u => { u.Ignored = true; u.Alias = "Robot"; });
        speech.Paused = true; // the queue fills but nothing plays
        foreach (var text in new[] { "First queued message from chat", "Second one, a bit longer to see how the list wraps" })
            speech.SpeakAsync(text, new SpeakOptions { UserName = "alice", Platform = "Twitch", Source = "chat" }).GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void Every_tab_renders_without_binding_errors_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new SpeechViewModel(_host.Get<SpeechService>(), new FakeInput());
            var window = SpeechWindow.Show(vm);
            UiPump.Pump();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
            Assert.Equal(TabNames.Length, tabs.ItemCount);

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                for (var i = 0; i < TabNames.Length; i++)
                {
                    vm.SelectedTab = i;
                    UiPump.Pump();
                    Assert.Equal(i, tabs.SelectedIndex);
                    UiPump.Save(window, $"speech-{name}-{TabNames[i]}");
                }
                vm.SelectedTab = 0;
            }

            Assert.Equal(2, vm.Overview.Queue.Count);
            Assert.Equal(2, vm.Users.Users.Count);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
