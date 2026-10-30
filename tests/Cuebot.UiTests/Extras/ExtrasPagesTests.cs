using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Extras;
using Cuebot.App.Views.Extras;
using Cuebot.App.Views.Extras.Desktop;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Credits;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Events;
using Cuebot.Core.Points;
using Cuebot.Core.Quotes;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Extras;

/// <summary>
/// Wave-4 extras pages (Quotes, Credits, Points, Hotkeys) and the in-app toast: editors write through to the core
/// services, and populated pages render without binding errors in both themes (screenshots in
/// tests/Cuebot.UiTests/screenshots, prefixed "extras-").
/// </summary>
public sealed class ExtrasPagesTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-extras-").FullName;
    private readonly ServiceProvider _services;
    private readonly FakeHotkeys _hotkeys = new();

    public ExtrasPagesTests()
    {
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddWave4Features();
        services.AddSingleton<IGlobalHotkeys>(_hotkeys);
        _services = services.BuildServiceProvider();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.BindingErrors.Clear();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();
    private EventBus Bus => Get<EventBus>();

    private static Window Open(Control view, object viewModel)
    {
        view.DataContext = viewModel;
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Saves dark and light screenshots.</summary>
    private static void SaveBothThemes(Window window, string name)
    {
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, ""), (ThemeVariant.Light, "-light") })
            {
                app.RequestedThemeVariant = theme;
                Pump();
                TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, $"extras-{name}{suffix}.png"));
            }
        }
        finally
        {
            app.RequestedThemeVariant = original;
            Pump();
        }
    }

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    private static BotEvent Activity(FeedKind kind, string user, string title, string platform = "twitch", string amount = "") =>
        new(ChatHub.ActivityEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["kind"] = kind.ToString(), ["user"] = user, ["title"] = title, ["detail"] = "", ["amount"] = amount,
        });

    private static BotEvent Chat(string user, string userId, string platform = "twitch", bool mod = false) =>
        new(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["user"] = user, ["userId"] = userId, ["message"] = "hi", ["isModerator"] = mod,
        });

    [AvaloniaFact]
    public void Quotes_page_searches_edits_deletes_and_imports()
    {
        var store = Get<QuoteStore>();
        store.Add("The cake is a lie", "GLaDOS", "1", "twitch", "Portal 2");
        store.Add("Do a barrel roll!", "Peppy", "2", "youtube", "Star Fox 64");
        store.Add("It's dangerous to go alone, take this.", "Old Man", "3", "kick", "The Legend of Zelda");
        var vm = new QuotesViewModel(store);
        var window = Open(new QuotesView(), vm);
        Assert.Equal(3, vm.Quotes.Count);
        Assert.Equal(3, window.GetVisualDescendants().OfType<ListBoxItem>().Count());
        SaveBothThemes(window, "quotes");

        vm.Search = "barrel";
        Assert.Equal("Do a barrel roll!", Assert.Single(vm.Quotes).Text);
        Assert.Contains("1 shown", vm.Summary);
        vm.Search = "";

        vm.Quotes[0].Game = "Portal";
        Assert.Equal("Portal", store.Get(1)!.Game);
        Assert.Equal(3, vm.Quotes.Count);   // editing does not rebuild the list

        vm.NewText = "Hey, listen!";
        vm.NewUser = "Navi";
        vm.AddQuoteCommand.Execute(null);
        Assert.Equal(4, store.Count);
        Assert.Equal("Navi", store.Get(4)!.User);

        vm.Quotes.First(q => q.Id == 2).DeleteCommand.Execute(null);
        Assert.Null(store.Get(2));
        vm.ReindexCommand.Execute(null);
        Assert.Equal([1, 2, 3], store.All().Select(q => q.Id));

        Assert.Equal(2, vm.Import("text,user\r\nimported one,Ann\r\n\"imported, two\",Bo\r\n", "more.csv", replace: false));
        Assert.Equal(5, vm.Quotes.Count);
        Assert.Equal(0, vm.Import("not json", "bad.json", replace: true));
        Assert.StartsWith("Import failed", vm.Status);
        Assert.Contains("imported, two", vm.ExportCsv());
        Pump();
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Credits_page_shows_live_credits_and_test_data()
    {
        var credits = Get<CreditsCollector>();
        var vm = new CreditsViewModel(credits);
        var window = Open(new CreditsView(), vm);
        Assert.True(vm.IsEmpty);

        Bus.Publish(Activity(FeedKind.Follow, "Alice", "Alice followed"));
        Bus.Publish(Activity(FeedKind.Tip, "Bob", "Bob cheered 500 bits", amount: "500 bits"));
        Bus.Publish(Activity(FeedKind.Raid, "Carol", "Carol raided with 30 viewers", "youtube"));
        Bus.Publish(Chat("Moddy", "9", mod: true));
        Pump();
        Assert.Equal(["follows", "cheers", "raids", "moderators", "chatters"], vm.Sections.Select(s => s.Id));
        Assert.Equal("Bob (500 bits)", vm.Sections[1].Names.Single());
        Assert.Contains("\"follows\"", vm.Json);
        SaveBothThemes(window, "credits");

        vm.ShowTestData = true;
        Assert.Equal(CreditsSections.Order, vm.Sections.Select(s => s.Id));
        Assert.Contains("sample", vm.Summary);
        SaveBothThemes(window, "credits-test-data");
        vm.ShowTestData = false;
        Assert.Equal(5, vm.Sections.Count);

        vm.RecordChatters = false;
        vm.AutoResetHours = 6;
        vm.IgnoredUsers = "Nightbot, @StreamElements";
        Assert.Contains(CreditsSections.Chatters, credits.Settings.DisabledSections);
        Assert.Equal(6, credits.Settings.AutoResetHours);
        Assert.Equal(["Nightbot", "StreamElements"], credits.Settings.IgnoredUsers);

        vm.ResetCommand.Execute(null);
        Assert.True(vm.IsEmpty);
        Pump();
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Points_page_shows_the_leaderboard_and_saves_settings()
    {
        var points = Get<PointsService>();
        foreach (var (name, id, platform) in new[] { ("Alice", "1", "twitch"), ("Bob", "2", "twitch"), ("Yuki", "y1", "youtube") })
            Bus.Publish(Chat(name, id, platform));
        points.Set("twitch", "1", "Alice", 1500);
        points.Set("twitch", "2", "Bob", 320);
        points.Set("youtube", "y1", "Yuki", 980);

        var vm = new PointsViewModel(points);
        var window = Open(new PointsView(), vm);
        Assert.Equal(["Alice", "Yuki", "Bob"], vm.Leaderboard.Select(r => r.UserName));
        Assert.Equal("1,500 points", vm.Leaderboard[0].Points);
        SaveBothThemes(window, "points");

        vm.CurrencyName = "cookies";
        vm.SingularName = "cookie";
        Assert.Equal("1,500 cookies", points.Format(1500));
        Pump();
        Assert.Equal("1,500 cookies", vm.Leaderboard[0].Points);
        vm.Thresholds = "100, 1000, nope";
        Assert.Equal([100L, 1000L], points.Settings.Thresholds);
        Assert.Contains("nope", vm.ThresholdsError);
        vm.MessageCooldownSeconds = 30;
        Assert.Equal(30, points.Settings.MessageCooldownSeconds);
        Assert.Equal("", points.Settings.Symbol);

        vm.PlatformFilter = "youtube";
        Assert.Equal("Yuki", Assert.Single(vm.Leaderboard).UserName);
        vm.PlatformFilter = "All";

        vm.AdjustPlatform = "twitch";
        vm.AdjustUser = "bob";
        vm.AdjustAmount = 80;
        vm.GiveCommand.Execute(null);
        Assert.Equal(400, points.Get("twitch", "2"));
        vm.AdjustUser = "ghost";
        vm.TakeCommand.Execute(null);
        Assert.Contains("Unknown user", vm.Status);

        vm.ResetAllCommand.Execute(null);
        Assert.Equal(1500, points.Get("twitch", "1"));   // first click only asks
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(0, points.Get("twitch", "1"));
        Pump();
        Assert.True(vm.IsEmpty);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Points_page_switches_currencies_and_edits_multipliers()
    {
        var points = Get<PointsService>();
        Bus.Publish(Chat("Alice", "1", "twitch"));
        points.Set("twitch", "1", "Alice", 1500);

        var vm = new PointsViewModel(points);
        var window = Open(new PointsView(), vm);
        Assert.Equal("points (default)", Assert.Single(vm.Currencies).Display);
        Assert.False(vm.CanRemoveCurrency);

        vm.NewCurrencyName = "gems";
        vm.AddCurrencyCommand.Execute(null);
        Pump();
        Assert.Equal(["points", "gems"], points.CurrencyNames);
        Assert.Equal("gems", vm.SelectedCurrency.Name);
        Assert.True(vm.CanRemoveCurrency);
        Assert.True(vm.IsEmpty);   // the gems leaderboard
        Assert.Equal("gems", vm.CurrencyName);

        vm.AdjustUser = "alice";
        vm.AdjustAmount = 40;
        vm.GiveCommand.Execute(null);
        Pump();
        Assert.Equal((1500L, 40L), (points.Get("twitch", "1"), points.Get("twitch", "1", "gems")));
        Assert.Equal("40 gems", Assert.Single(vm.Leaderboard).Points);

        vm.VipMultiplier = 2;
        vm.GroupMultipliers = "Regulars=1.5, oops";
        Assert.Equal(2m, points.GetCurrency("gems")!.VipMultiplier);
        Assert.Contains("oops", vm.GroupMultipliersError);
        vm.GroupMultipliers = "Regulars=1.5";
        Assert.Equal(1.5m, points.GetGroupMultiplier("regulars", "gems"));
        Assert.Equal(1m, points.Settings.VipMultiplier);   // the default currency is untouched

        vm.CurrencyName = "points";   // taken by the default currency: rejected
        Assert.Contains("already used", vm.Status);
        vm.CurrencyName = "rubies";
        Assert.Equal(["points", "rubies"], points.CurrencyNames);
        Assert.Equal("rubies", vm.SelectedCurrency.Name);
        SaveBothThemes(window, "points-currencies");

        vm.SelectedCurrency = vm.Currencies[0];
        Pump();
        Assert.Equal("points", vm.CurrencyName);
        Assert.Equal("1,500 points", Assert.Single(vm.Leaderboard).Points);

        vm.SelectedCurrency = vm.Currencies[1];
        vm.RemoveCurrencyCommand.Execute(null);
        Pump();
        Assert.Single(points.Currencies);
        Assert.Single(vm.Currencies);
        Assert.Equal("points", vm.SelectedCurrency.Name);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hotkeys_page_groups_hotkeys_and_has_a_set_action_context_menu()
    {
        var service = Get<HotkeyService>();
        await service.AddGroupAsync("Scenes");
        await service.SaveAsync(new HotkeyDefinition { Name = "Intro", Gesture = "Ctrl+F1", Group = "Scenes" });
        await service.SaveAsync(new HotkeyDefinition { Name = "Outro", Gesture = "Ctrl+F2", Group = "Scenes" });
        await service.SaveAsync(new HotkeyDefinition { Name = "Clip", Gesture = "Ctrl+F3" });
        await service.StartAsync();
        var vm = new HotkeysViewModel(service, Get<JsonFile<DesktopSettings>>(), () => ["Scene: Gameplay", "Clip it"]);
        var view = new HotkeysView();
        var window = Open(view, vm);

        Assert.Equal(["", "Scenes"], vm.Sections.Select(s => s.Name));
        var ungrouped = vm.Sections[0];
        var scenes = vm.Sections[1];
        Assert.Equal(["Clip"], ungrouped.Hotkeys.Select(h => h.Name));
        Assert.Equal(["Intro", "Outro"], scenes.Hotkeys.Select(h => h.Name));
        Assert.True(ungrouped.ShowHeader);
        Assert.Equal("2 hotkeys", scenes.CountText);

        // + Group, rename in the header, move a hotkey there with its Group box.
        vm.AddGroupCommand.Execute(null);
        var replay = vm.Sections[2];
        Assert.Equal("Group 2", replay.Name);
        replay.Name = "Replay";
        Assert.Equal(["", "Scenes", "Replay"], vm.GroupChoices);
        var clip = vm.Hotkeys.Single(h => h.Name == "Clip");
        clip.Group = "Replay";
        Pump();
        Assert.Equal(["Clip"], replay.Hotkeys.Select(h => h.Name));
        Assert.Empty(ungrouped.Hotkeys);
        Assert.False(ungrouped.ShowHeader);
        Assert.Equal("Replay", service.Hotkeys.Single(h => h.Name == "Clip").Group);
        replay.Name = "scenes";   // taken: refused, the old name stays
        Assert.Equal("Replay", replay.Name);
        Assert.Contains("already a group", vm.Status);
        replay.Name = "Replays";
        Pump();
        Assert.Equal("Replays", clip.Group);
        Assert.Equal("Replays", service.Hotkeys.Single(h => h.Name == "Clip").Group);

        // Disabling a group unregisters its hotkeys.
        scenes.Enabled = false;
        await WaitFor(() => { Pump(); return vm.Hotkeys.Single(h => h.Name == "Intro").State == "Group 'Scenes' is disabled"; });
        Assert.DoesNotContain(_hotkeys.Applied[^1], r => r.Gesture.ToString() == "Ctrl+F1");
        Assert.Contains(_hotkeys.Applied[^1], r => r.Gesture.ToString() == "Ctrl+F3");
        SaveBothThemes(window, "hotkey-groups");

        // The card's context menu: Set Action, Group and Enabled.
        var card = view.GetVisualDescendants().OfType<Border>().First(b => b.DataContext == clip && b.ContextMenu is not null);
        var menu = card.ContextMenu!;
        card.RaiseEvent(new ContextRequestedEventArgs());
        Pump();
        MenuItem Entry(string header) => menu.Items.OfType<MenuItem>().First(m => (string?)m.Header == header);
        MenuItem Choice(MenuItem parent, string label)
        {
            parent.Open();
            Pump();
            return parent.GetRealizedContainers().OfType<MenuItem>().First(m => (string?)m.Header == label);
        }
        void Click(MenuItem item)
        {
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
        }

        Assert.Equal(["(none)", "Clip it", "Scene: Gameplay"],
            Entry("Set Action").Items.OfType<HotkeyMenuChoice>().Select(c => c.Label));
        Click(Choice(Entry("Set Action"), "Clip it"));
        Assert.Equal("Clip it", clip.Action);
        Assert.Equal("Clip it", service.Hotkeys.Single(h => h.Name == "Clip").Action);
        menu.Close();
        card.RaiseEvent(new ContextRequestedEventArgs());   // right-click: the lists are rebuilt on opening
        Pump();
        Assert.True(Entry("Set Action").Items.OfType<HotkeyMenuChoice>().Single(c => c.Label == "Clip it").IsSelected);
        Assert.Equal(["(no group)", "Scenes", "Replays", "New group"], Entry("Group").Items.OfType<HotkeyMenuChoice>().Select(c => c.Label));
        Click(Choice(Entry("Group"), "Scenes"));
        Assert.Equal(["Intro", "Outro", "Clip"], scenes.Hotkeys.Select(h => h.Name));
        Assert.Equal("Scenes", service.Hotkeys.Single(h => h.Name == "Clip").Group);
        menu.Close();

        card = view.GetVisualDescendants().OfType<Border>().First(b => b.DataContext == clip && b.ContextMenu is not null);
        menu = card.ContextMenu!;
        card.RaiseEvent(new ContextRequestedEventArgs());
        Pump();
        Assert.True(Entry("Enabled").IsChecked);
        Click(Entry("Enabled"));
        Assert.False(clip.Enabled);
        Assert.False(service.Hotkeys.Single(h => h.Name == "Clip").Enabled);
        menu.Close();
        Pump();

        // Deleting a group keeps its hotkeys, without a group.
        scenes.DeleteCommand.Execute(null);
        Pump();
        Assert.Equal(["", "Replays"], vm.Sections.Select(s => s.Name));
        Assert.Equal(["Intro", "Outro", "Clip"], ungrouped.Hotkeys.Select(h => h.Name));
        Assert.All(service.Hotkeys, h => Assert.Equal("", h.Group));
        Assert.Equal(["Replays"], service.Groups.Select(g => g.Name));
        await WaitFor(() => { Pump(); return vm.Hotkeys.Single(h => h.Name == "Intro").Registered; });

        // A fresh page (after a restart) shows the saved groups.
        var reopened = new HotkeysViewModel(service, Get<JsonFile<DesktopSettings>>());
        Assert.Equal(["", "Replays"], reopened.Sections.Select(s => s.Name));
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Hotkeys_page_records_a_combo_and_registers_it()
    {
        var service = Get<HotkeyService>();
        await service.StartAsync();
        var vm = new HotkeysViewModel(service, Get<JsonFile<DesktopSettings>>(), () => ["Scene: Gameplay", "Clip it"]);
        var view = new HotkeysView();
        var window = Open(view, vm);

        vm.AddHotkeyCommand.Execute(null);
        var item = Assert.Single(vm.Hotkeys);
        Assert.True(vm.IsRecording);
        view.Focus();
        Pump();
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);   // modifier alone: keep waiting
        Assert.True(vm.IsRecording);
        window.KeyPress(Key.F5, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F5, null);
        Pump();
        Assert.False(vm.IsRecording);
        Assert.Equal("Ctrl+Shift+F5", item.Gesture);
        item.Name = "Clip";
        item.Action = "Clip it";
        await WaitFor(() => _hotkeys.Applied.LastOrDefault()?.Any(r => r.Gesture.ToString() == "Ctrl+Shift+F5") == true);
        await WaitFor(() => { Pump(); return item.Registered; });
        Assert.Equal("Registered", item.State);
        Assert.Equal("Clip it", service.Hotkeys.Single().Action);

        vm.AddHotkeyCommand.Execute(null);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        Assert.False(vm.IsRecording);
        vm.Hotkeys[1].Gesture = "Ctrl+Nope";
        Assert.Contains("not a supported key", vm.Hotkeys[1].GestureError);
        vm.Hotkeys[1].Enabled = false;

        vm.NotificationTarget = "In-app";
        vm.WatchClipboard = true;
        Assert.Equal(NotificationTarget.InApp, Get<JsonFile<DesktopSettings>>().Value.NotificationTarget);
        Assert.True(Get<JsonFile<DesktopSettings>>().Value.WatchClipboard);

        var pressed = new List<string>();
        using var _ = Bus.Subscribe(e => { if (e.Type == HotkeyEvents.Pressed) pressed.Add((string)e.Args["hotkey"]!); });
        item.TestCommand.Execute(null);
        Assert.Equal(["Ctrl+Shift+F5"], pressed);
        Pump();
        SaveBothThemes(window, "hotkeys");

        vm.Hotkeys[1].RemoveCommand.Execute(null);
        Assert.Single(service.Hotkeys);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void In_app_toast_renders_and_headless_clipboard_reports_unavailable()
    {
        var toast = AvaloniaToastPresenter.CreateWindow(new DesktopNotification("Alice followed!", "Welcome to the stream, Alice.") { DurationSeconds = 5 });
        toast.Show();
        Pump();
        Assert.True(toast.Topmost);
        Assert.False(toast.ShowActivated);
        Assert.True(toast.Bounds.Height > 0);
        SaveBothThemes(toast, "toast");
        toast.Close();

        // The headless test app has no desktop lifetime, like headless mode: the services say so instead of crashing.
        Assert.False(new AvaloniaClipboardService().IsAvailable);
        Assert.False(new AvaloniaToastPresenter().IsAvailable);
        AssertNoBindingErrors();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeHotkeys : IGlobalHotkeys
    {
        private readonly List<IReadOnlyList<HotkeyRegistration>> _applied = [];

        public IReadOnlyList<IReadOnlyList<HotkeyRegistration>> Applied
        {
            get { lock (_applied) return _applied.ToArray(); }
        }

        public string BackendName => "Fake";
        public string Status => "test backend";
        public event Action<string>? Pressed { add { } remove { } }

        public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
        {
            lock (_applied) _applied.Add(hotkeys);
            return Task.FromResult<IReadOnlyList<HotkeyBindingResult>>(hotkeys.Select(h => new HotkeyBindingResult(h.Id, true)).ToArray());
        }

        public void Dispose() { }
    }
}
