using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;
using Cuebot.App.Views;
using Cuebot.App.Views.Chat;
using Cuebot.Speech;
using Cuebot.Speech.Emotes;
using Cuebot.Core.Chat.Emotes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Chat;

/// <summary>
/// The combined chat page with chat extras: inline emote images with text fallback, pronouns, highlights, the message
/// actions menu, emote autocomplete, slash commands and quick actions. Screenshots go to tests/Cuebot.UiTests/screenshots.
/// </summary>
public sealed class ChatPageTests : IDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly ChatPageFixture _f = new();

    private static (Window Window, ChatView View) Show(ChatViewModel vm)
    {
        var view = new ChatView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 760 };
        window.Show();
        ChatPageFixture.Pump();
        return (window, view);
    }

    private static void Save(Window window, string name)
    {
        ChatPageFixture.Pump();
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private void SeedChat()
    {
        _f.Chat("Alice", "Kappa KEKW RainTime Missing hello 😀 catJAM", emotes: "25:0-4");
        _f.Chat("Bob", "@streamer is there a giveaway today?");
        _f.Chat("Newbie", "first time here, hi all", firstMessage: true);
        _f.Chat("Mod", "keep it friendly catJAM");
        _f.Chat("Kicker", "from kick [emote:37226:KEKW]", platform: "Kick");
    }

    [AvaloniaFact]
    public void Emotes_render_inline_as_images_with_text_fallback_in_both_themes()
    {
        TestApp.BindingErrors.Clear();
        var vm = _f.CreateViewModel();
        SeedChat();
        var (window, view) = Show(vm);

        var alice = vm.Messages[0];
        Assert.Equal(["Kappa", "KEKW", "Missing", "catJAM"], alice.Emotes.Select(e => e.Code));
        Assert.Equal(["Twitch", "7TV", "7TV", "7TV"], alice.Emotes.Select(e => e.Provider));
        var kekw = alice.Emotes.Single(e => e.Code == "KEKW");
        Assert.Equal("RainTime", Assert.Single(kekw.Overlays).Code);   // zero-width stacks on the previous emote

        ChatPageFixture.Until(() => alice.Emotes.Count(e => e.HasImage) == 3 && kekw.Overlays[0].HasImage);
        Assert.False(alice.Emotes.Single(e => e.Code == "Missing").HasImage);
        var kick = vm.Messages.Single(m => m.Platform == "Kick");
        Assert.Equal("KEKW", Assert.Single(kick.Emotes).Code);
        Assert.Equal("Kick", kick.Emotes.Single().Provider);

        ChatPageFixture.Pump();
        var text = view.GetVisualDescendants().OfType<ChatMessageText>().First(t => t.Line == alice);
        var containers = text.Inlines!.OfType<InlineUIContainer>().ToArray();
        Assert.Equal(4, containers.Length);
        var missing = (Grid)containers.Single(c => ((Grid)c.Child).Tag is EmoteSegment { Code: "Missing" }).Child;
        Assert.True(missing.Children.OfType<TextBlock>().Single().IsVisible);           // fallback text shows
        Assert.False(missing.Children.OfType<Image>().Single().IsVisible);
        var kekwGrid = (Grid)containers.Single(c => ((Grid)c.Child).Tag is EmoteSegment { Code: "KEKW" }).Child;
        Assert.Equal(2, kekwGrid.Children.OfType<Image>().Count(i => i.IsVisible));     // emote + overlay
        Assert.Contains(text.Inlines!.OfType<Run>(), r => r.Text!.Contains("hello 😀", StringComparison.Ordinal));

        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            Save(window, "chat-emotes-dark");
            app.RequestedThemeVariant = ThemeVariant.Light;
            Save(window, "chat-emotes-light");
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Pronouns_show_next_to_twitch_names_and_can_be_turned_off()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Alice", "hi");
        _f.Chat("Bob", "hey");
        _f.Chat("Carol", "no pronouns set");
        var (window, view) = Show(vm);

        ChatPageFixture.Until(() => vm.Messages[0].Pronouns is not null && vm.Messages[1].Pronouns is not null);
        Assert.Equal("They/Them", vm.Messages[0].Pronouns);
        Assert.Equal("He/They", vm.Messages[1].Pronouns);
        Assert.Null(vm.Messages[2].Pronouns);
        ChatPageFixture.Pump();
        var text = view.GetVisualDescendants().OfType<ChatMessageText>().First(t => t.Line == vm.Messages[0]);
        Assert.Contains(text.Inlines!.OfType<Run>(), r => r.Text == " (They/Them)");

        vm.Options.ShowPronouns = false;
        Assert.All(vm.Messages, m => Assert.Null(m.Pronouns));
        Assert.False(_f.Settings.Value.ShowPronouns);
        window.Close();
    }

    [AvaloniaFact]
    public void Highlight_rules_mark_mentions_keywords_and_first_time_chatters()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Bob", "@streamer hello");
        _f.Chat("Ann", "is the GIVEAWAY live?");
        _f.Chat("Newbie", "hi", firstMessage: true);
        _f.Chat("Zed", "streamers are cool");   // not a whole-word mention
        ChatPageFixture.Pump();

        Assert.Equal([HighlightKind.Mention, HighlightKind.Keyword, HighlightKind.FirstTime, HighlightKind.None],
            vm.Messages.Select(m => m.Highlight.Kind));
        Assert.Equal("Mentions streamer", vm.Messages[0].HighlightReason);

        vm.Options.Keywords = "cool";
        vm.Options.HighlightFirstTime = false;
        Assert.Equal([HighlightKind.Mention, HighlightKind.None, HighlightKind.None, HighlightKind.Keyword],
            vm.Messages.Select(m => m.Highlight.Kind));
        Assert.Equal(["cool"], _f.Settings.Value.Highlights.Keywords);
    }

    [AvaloniaFact]
    public void Message_menu_offers_only_available_moderation_plus_quick_actions()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Bob", "spam spam", id: "msg-1");
        _f.Chat("Kicker", "hello", platform: "Kick");
        var (window, _) = Show(vm);
        var bob = vm.Messages[0];

        Assert.Equal(["Delete message", "Timeout 1 minute", "Timeout 10 minutes", "Ban Bob", "-", "Shout out", "Hello", "-", "Copy message to send box"],
            vm.GetMenu(bob).Select(e => e.Label));
        Assert.Equal(["Shout out", "Hello", "-", "Copy message to send box"], vm.GetMenu(vm.Messages[1]).Select(e => e.Label));

        vm.GetMenu(bob).Single(e => e.Label == "Delete message").Run!().GetAwaiter().GetResult();
        vm.GetMenu(bob).Single(e => e.Label == "Timeout 10 minutes").Run!().GetAwaiter().GetResult();
        vm.GetMenu(bob).Single(e => e.Label == "Ban Bob").Run!().GetAwaiter().GetResult();
        Assert.Equal(["delete msg-1", "timeout id-bob bob 600", "ban id-bob bob"], _f.Moderator.Calls);
        Assert.True(bob.IsDeleted);
        Assert.DoesNotContain(vm.GetMenu(bob), e => e.Label == "Delete message");

        _f.Moderator.Available = Cuebot.Core.Chat.Moderation.ChatModerationActions.Timeout;
        Assert.Equal(["Timeout 1 minute", "Timeout 10 minutes"], vm.GetMenu(bob).Where(e => e.Label.StartsWith("Timeout") || e.Label.StartsWith("Ban")).Select(e => e.Label));

        var menu = ChatView.BuildMenu(vm, bob);
        Assert.Equal(["Timeout 1 minute", "Timeout 10 minutes"], menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).Take(2));
        Assert.Equal(2, menu.Items.OfType<Separator>().Count());
        Save(window, "chat-deleted-message");
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_a_colon_offers_emotes_and_tab_completes_them()
    {
        TestApp.BindingErrors.Clear();
        var vm = _f.CreateViewModel();
        _f.Chat("Alice", "Kappa", emotes: "25:0-4");   // native emotes seen in chat join the list
        var (window, view) = Show(vm);
        var box = Named<TextBox>(view, "DraftBox");
        box.Focus();

        vm.Draft = "hello :ke";
        ChatPageFixture.Pump();
        Assert.True(vm.IsSuggesting);
        Assert.True(Named<Border>(view, "SuggestionPanel").IsVisible);
        Assert.Equal(["KEKW", "KEKBye"], vm.Suggestions.Select(s => s.Code));
        Assert.Equal("KEKW", vm.SelectedSuggestion!.Code);
        ChatPageFixture.Until(() => vm.Suggestions.All(s => s.HasImage));
        Save(window, "chat-autocomplete");

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal("KEKBye", vm.SelectedSuggestion!.Code);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        ChatPageFixture.Pump();
        Assert.Equal("hello KEKBye ", vm.Draft);
        Assert.False(vm.IsSuggesting);
        Assert.Empty(_f.Sender.Sent);   // Tab completed, nothing was sent

        vm.Draft = ":kap";
        Assert.Equal("Kappa", vm.Suggestions.First().Code);
        Assert.True(vm.AcceptSuggestion());
        Assert.Equal("Kappa ", vm.Draft);

        vm.Draft = "a:b";       // not a word starting with ':'
        Assert.False(vm.IsSuggesting);
        vm.Draft = "time 10:30";
        Assert.False(vm.IsSuggesting);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Slash_commands_clear_run_actions_and_announce()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Bob", "hello there", id: "m-bob");
        ChatPageFixture.Pump();
        vm.SelectedLine = vm.Messages[0];

        vm.Draft = "/action Say Hello big wave";
        await vm.SendCommand.ExecuteAsync(null);
        var run = Assert.Single(_f.Recorder.Runs);
        Assert.Equal("Say Hello", run.Action);
        Assert.Equal("big wave", run.Args["rawInput"]);
        Assert.Equal("wave", run.Args["input1"]);
        Assert.Equal("Bob", run.Args["user"]);
        Assert.Equal("m-bob", run.Args["messageId"]);
        Assert.Equal("", vm.Draft);

        vm.Draft = "/action Nope";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("No action named 'Nope'", vm.Status);

        vm.SendTarget = "Twitch";
        vm.Draft = "/announce Stream starts soon";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(("Stream starts soon", Cuebot.Core.Chat.Moderation.ChatAnnouncementKind.Announcement), Assert.Single(_f.Announcer.Posts));
        vm.Draft = "/me waves";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("not supported", vm.Status, StringComparison.Ordinal);

        vm.Draft = "/help";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("/action", vm.Status, StringComparison.Ordinal);

        vm.Draft = "/bogus";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.StartsWith("Unknown command /bogus", vm.Status, StringComparison.Ordinal);

        vm.Draft = "//shrug";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(["/shrug"], _f.Sender.Sent);

        vm.Draft = "/clear";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(vm.Messages);
        Assert.Null(vm.SelectedLine);
        Assert.Single(_f.Hub.RecentMessages());   // only the view was cleared
    }

    [AvaloniaFact]
    public async Task Quick_actions_run_with_the_selected_message_and_respect_scope()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Bob", "great stream", id: "m-1");
        var (window, view) = Show(vm);

        Assert.Equal(["Shout out", "Hello"], vm.QuickActions.Select(q => q.Label));
        Assert.False(vm.QuickActions[0].CanRun);   // user scope needs a selection
        Assert.True(vm.QuickActions[1].CanRun);
        var bar = Named<ItemsControl>(view, "QuickActionBar");
        Assert.True(bar.IsVisible);

        await vm.QuickActions[1].RunCommand.ExecuteAsync(null);
        var global = Assert.Single(_f.Recorder.Runs);
        Assert.Equal("Say Hello", global.Action);
        Assert.False(global.Args.ContainsKey("user"));

        vm.SelectedLine = vm.Messages[0];
        Assert.True(vm.QuickActions[0].CanRun);
        await vm.QuickActions[0].RunCommand.ExecuteAsync(null);
        var shout = _f.Recorder.Runs[^1];
        Assert.Equal("Shoutout", shout.Action);
        Assert.Equal("bob", shout.Args["userName"]);
        Assert.Equal("id-bob", shout.Args["userId"]);
        Assert.Equal("Twitch", shout.Args["platform"]);
        Assert.Equal("Shout out", shout.Args["quickAction"]);
        Assert.False(shout.Args.ContainsKey("message"));   // user scope: no message args

        // Editing in the options panel updates the bar and the saved settings.
        vm.ShowOptions = true;
        vm.Options.AddQuickActionCommand.Execute(null);
        Assert.Equal(3, vm.QuickActions.Count);
        vm.Options.QuickActions[2].Label = "Lurk";
        vm.Options.QuickActions[2].Scope = QuickActionScope.Message;
        Assert.Equal("Lurk", _f.Settings.Value.QuickActions[2].Label);
        Assert.Equal("Lurk", vm.QuickActions[2].Label);
        ChatPageFixture.Pump();
        Assert.True(Named<Border>(view, "OptionsPanel").IsVisible);
        Save(window, "chat-options");
        vm.Options.QuickActions[2].RemoveCommand.Execute(null);
        Assert.Equal(2, _f.Settings.Value.QuickActions.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void Turning_emotes_off_shows_plain_text_and_new_sets_rerender_lines()
    {
        var vm = _f.CreateViewModel();
        _f.Chat("Alice", "KEKW then NewOne");
        ChatPageFixture.Pump();
        Assert.Single(vm.Messages[0].Emotes);

        vm.Options.ShowEmotes = false;
        Assert.Empty(vm.Messages[0].Emotes);
        vm.Options.ShowEmotes = true;
        Assert.Single(vm.Messages[0].Emotes);
    }

    [AvaloniaFact]
    public void Page_works_without_any_chat_extras()
    {
        TestApp.BindingErrors.Clear();
        var vm = new ChatViewModel(_f.Hub);
        _f.Chat("Alice", "KEKW plain");
        var (window, _) = Show(vm);
        Assert.Empty(vm.Messages[0].Emotes);
        Assert.Equal(["Copy message to send box"], vm.GetMenu(vm.Messages[0]).Select(e => e.Label));
        vm.Draft = ":ke";
        Assert.False(vm.IsSuggesting);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Services_from_the_app_host_tolerate_missing_chat_extras()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-ui-chathost-").FullName;
        await using var host = Cuebot.App.AppHost.Create(new Cuebot.Core.AppPaths(dir));
        var services = ChatPageServices.From(host);

        Assert.NotNull(services.Engine);
        Assert.NotNull(services.Settings);
        Assert.NotNull(services.ActionNames);
        var vm = new ChatViewModel(host.Get<Cuebot.Core.Chat.ChatHub>(), services);
        Assert.Equal(services.Emotes is not null, vm.Options.HasEmoteService);
    }

    [Fact]
    public void Speech_emote_name_providers_expose_each_third_party_provider()
    {
        var services = new ServiceCollection().AddSingleton(_f.Emotes).AddSpeechEmoteNames().BuildServiceProvider();
        var providers = services.GetServices<IEmoteNameProvider>().ToArray();

        Assert.Equal(EmoteProviders.ThirdParty, providers.Select(p => p.Source));
        Assert.Contains("KEKW", providers.Single(p => p.Source == "7TV").GetEmoteNames("Twitch"));
        Assert.Empty(providers.Single(p => p.Source == "BTTV").GetEmoteNames("Twitch"));
    }

    [Fact]
    public void Slash_command_parsing()
    {
        Assert.Equal(new SlashCommand("action", "Do It now"), SlashCommand.Parse("  /Action Do It now "));
        Assert.Null(SlashCommand.Parse("//escaped"));
        Assert.Null(SlashCommand.Parse("/ space"));
        Assert.Null(SlashCommand.Parse("plain"));
        Assert.Equal("/escaped", SlashCommand.Unescape("//escaped"));
        Assert.Equal(("Say Hello", "to all"), SlashCommand.SplitAction("Say Hello to all", n => n is "Say Hello" or "Say"));
        Assert.Null(SlashCommand.SplitAction("Nothing here", _ => false));
    }

    public void Dispose() => _f.Dispose();
}
