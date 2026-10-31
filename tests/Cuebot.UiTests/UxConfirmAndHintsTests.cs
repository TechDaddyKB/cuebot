using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Extras;
using Cuebot.App.ViewModels.Obs;
using Cuebot.App.ViewModels.Speech;
using Cuebot.App.ViewModels.Twitch;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Users;
using Cuebot.Obs;
using Cuebot.Speech;
using Cuebot.Twitch;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests;

/// <summary>
/// Spots that confused streamers: buttons that go public ask for a second click, a command word that something else
/// already answers is pointed out, chat mode ticks that were not sent yet say so, and speech options use plain words.
/// </summary>
public sealed class UxConfirmAndHintsTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-ux-").FullName;
    private readonly AppHost _host;

    public UxConfirmAndHintsTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private static Window Show(Control view, object vm)
    {
        view.DataContext = vm;
        var window = new Window
        {
            Content = view,
            Width = 1360,
            Height = 1300,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static Button ButtonWithText(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, text));

    private static TextBlock? VisibleText(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == text);

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    [AvaloniaFact]
    public async Task Ban_needs_a_second_click_within_five_seconds()
    {
        TestApp.BindingErrors.Clear();
        var vm = new TwitchViewModel(_host.Get<TwitchService>()) { SelectedTab = 4 };
        var moderation = vm.Moderation;
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        moderation.BanConfirm.Clock = () => now;
        var window = Show(new Cuebot.App.Views.TwitchView(), vm);
        moderation.User = "@troll";

        await moderation.BanCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.True(moderation.BanConfirm.IsArmed);
        Assert.Equal("", moderation.Error);                     // nothing was sent to Twitch yet
        Assert.Equal("Click again within 5 seconds to ban troll.", moderation.Info);
        Assert.Contains("danger", ButtonWithText(window, "Click again to ban").Classes);

        // Too late: the old click no longer counts, it asks again.
        now = now.AddSeconds(6);
        await moderation.BanCommand.ExecuteAsync(null);
        Assert.True(moderation.BanConfirm.IsArmed);
        Assert.Equal("", moderation.Error);

        // Another name: asks again for that name.
        moderation.User = "someone";
        await moderation.BanCommand.ExecuteAsync(null);
        Assert.Equal("", moderation.Error);

        // Second click in time: it really tries (and here fails, nobody is signed in).
        now = now.AddSeconds(2);
        await moderation.BanCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.False(moderation.BanConfirm.IsArmed);
        Assert.Contains("Sign in", moderation.Error);
        ButtonWithText(window, "Ban");

        // Clear chat asks the same way.
        await moderation.ClearChatCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.Equal("", moderation.Error);
        ButtonWithText(window, "Click again to clear chat");
        await moderation.ClearChatCommand.ExecuteAsync(null);
        Assert.Contains("Sign in", moderation.Error);

        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Start_stream_needs_a_second_click()
    {
        TestApp.BindingErrors.Clear();
        var vm = new ObsViewModel(_host.Get<ObsService>());
        var connection = vm.SelectedConnection;
        var window = Show(new Cuebot.App.Views.ObsView(), vm);

        await connection.ToggleStreamCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.True(connection.StreamConfirm.IsArmed);
        Assert.Equal("Click again to start / stop stream", connection.StreamConfirm.Text);
        Assert.Equal("", connection.Error);                     // OBS was not asked yet

        await connection.ToggleStreamCommand.ExecuteAsync(null);
        UiPump.Pump();
        Assert.False(connection.StreamConfirm.IsArmed);
        Assert.Equal("Start / stop stream", connection.StreamConfirm.Text);
        Assert.NotEqual("", connection.Error);                  // asked OBS, which is not connected here

        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Cancel_and_refund_needs_a_second_click()
    {
        var giveaways = _host.Get<Cuebot.Core.Giveaways.Giveaways>();
        giveaways.AnnounceWinners = false;
        var vm = new GiveawaysStoreViewModel(giveaways, _host.Get<Cuebot.Core.Store.PointsStore>());
        vm.Prize = "A Steam game";
        vm.OpenGiveawayCommand.Execute(null);
        UiPump.Pump();

        vm.CancelGiveawayCommand.Execute(null);
        UiPump.Pump();
        Assert.True(vm.GiveawayRunning);
        Assert.Equal("Click again to cancel and refund", vm.CancelConfirm.Text);
        Assert.StartsWith("Click again within 5 seconds", vm.Status);

        vm.CancelGiveawayCommand.Execute(null);
        UiPump.Pump();
        Assert.False(vm.GiveawayRunning);
        Assert.Equal("Cancelled; tickets were refunded.", vm.GiveawaySummary);
    }

    [AvaloniaFact]
    public void A_command_word_that_an_action_or_command_also_answers_is_pointed_out()
    {
        TestApp.BindingErrors.Clear();
        var profile = _host.Get<ProfileService>();
        var store = _host.Get<CommandStore>();
        var hello = profile.Profile.Actions.Single(a => a.Name == "!hello");   // the built-in example action
        Assert.Equal("Examples", hello.Group);
        var page = new CommandsViewModel(store, _host.Get<GroupStore>(), profile);
        var window = Show(new Cuebot.App.Views.CommandsView(), page);

        page.NewCommandCommand.Execute(null);
        var editor = page.Editor!;
        editor.AliasesText = "!lurk";
        Assert.False(editor.HasDuplicateWarning);

        const string actionWarning =
            "!hello is also answered by the action '!hello' (Examples). Turn one off, or viewers get two replies.";
        editor.AliasesText = "!lurk\n!HELLO";
        UiPump.Pump();
        Assert.Equal(actionWarning.Replace("!hello is", "!HELLO is"), editor.DuplicateWarning);
        editor.AliasesText = "!lurk\n!hello";
        UiPump.Pump();
        Assert.Equal(actionWarning, editor.DuplicateWarning);
        Assert.NotNull(VisibleText(window, actionWarning));

        // Turning the action off removes the warning; the page follows the live profile.
        hello.Enabled = false;
        profile.NotifyChanged();
        UiPump.Pump();
        Assert.False(editor.HasDuplicateWarning);
        Assert.Null(VisibleText(window, actionWarning));

        // Another enabled command with the same word.
        store.Save(new CommandDefinition { Name = "Lurk", Group = "Fun", Commands = ["!lurk"] });
        UiPump.Pump();
        Assert.Equal("!lurk is also answered by the command 'Lurk' (Fun). Turn one off, or viewers get two replies.",
            page.Editor!.DuplicateWarning);

        // A command that is off does not answer anybody.
        page.Editor.Enabled = false;
        Assert.False(page.Editor.HasDuplicateWarning);

        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Chat_mode_ticks_that_were_not_applied_say_so()
    {
        TestApp.BindingErrors.Clear();
        var vm = new TwitchViewModel(_host.Get<TwitchService>()) { SelectedTab = 4 };
        var moderation = vm.Moderation;
        var window = Show(new Cuebot.App.Views.TwitchView(), vm);
        const string pending = "Not sent yet: press Apply to change your chat.";
        var apply = ButtonWithText(window, "Apply");

        Assert.False(moderation.HasPendingChatModes);
        Assert.Null(VisibleText(window, pending));
        Assert.DoesNotContain("accent", apply.Classes);

        moderation.EmoteMode = true;
        UiPump.Pump();
        Assert.True(moderation.HasPendingChatModes);
        Assert.NotNull(VisibleText(window, pending));
        Assert.Contains("accent", apply.Classes);

        // Only the seconds of a mode that is on count.
        moderation.EmoteMode = false;
        moderation.SlowSeconds = 60;
        UiPump.Pump();
        Assert.False(moderation.HasPendingChatModes);
        Assert.Null(VisibleText(window, pending));
        moderation.SlowMode = true;
        Assert.True(moderation.HasPendingChatModes);

        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Speech_options_show_plain_words_but_keep_their_values()
    {
        Assert.Equal("Drop the newest message", SpeechChoiceLabels.Label(QueueDropPolicy.DropNewest));
        Assert.Equal("Drop the oldest message", SpeechChoiceLabels.Label(QueueDropPolicy.DropOldest));
        Assert.Equal("Remove the word", SpeechChoiceLabels.Label(BadWordAction.Strip));
        Assert.Equal("Skip the message", SpeechChoiceLabels.Label(UrlMode.Skip));

        TestApp.BindingErrors.Clear();
        var vm = new SpeechViewModel(_host.Get<SpeechService>(), new FakeInput());
        var window = SpeechWindow.Show(vm);
        vm.SelectedTab = 5;   // filters
        UiPump.Pump();
        var profile = vm.Filters.SelectedProfile!;

        var badWords = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, profile.BadWordActions));
        badWords.SelectedItem = BadWordAction.Replace;
        UiPump.Pump();
        Assert.Equal(BadWordAction.Replace, profile.BadWordAction);   // the stored value is unchanged
        var shown = badWords.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
        Assert.Contains("Replace the word", shown);
        Assert.DoesNotContain("Replace", shown);

        AssertNoBindingErrors();
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
