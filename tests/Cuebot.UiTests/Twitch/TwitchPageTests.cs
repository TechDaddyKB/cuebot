using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Twitch;
using Cuebot.Core;
using Cuebot.Twitch;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Twitch;

/// <summary>The Twitch page: every tab renders in both themes with the real composition root, and the tabs write settings through.</summary>
public sealed class TwitchPageTests : IAsyncDisposable
{
    private static readonly string[] TabNames = ["accounts", "channel", "rewards", "polls", "moderation", "settings"];

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-twitchpage-").FullName;
    private readonly AppHost _host;

    public TwitchPageTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private TwitchService Twitch => _host.Get<TwitchService>();

    private static Window Show(TwitchViewModel vm)
    {
        var window = new Window
        {
            Content = new Cuebot.App.Views.TwitchView { DataContext = vm },
            Width = 1360,
            Height = 1300,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    [AvaloniaFact]
    public void Every_tab_renders_without_binding_errors_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new TwitchViewModel(Twitch);
            vm.Rewards.NewCommand.Execute(null);
            vm.Polls.Outcomes.Add("Yes");
            var window = Show(vm);
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
                    UiPump.Save(window, $"twitch-{name}-{TabNames[i]}");
                }
                vm.SelectedTab = 0;
            }

            Assert.Equal("Not signed in", vm.StatusText);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void Settings_tab_stores_the_requested_scopes()
    {
        var settings = new TwitchViewModel(Twitch).Settings;
        Assert.All(settings.Scopes, s => Assert.True(s.IsRequested));
        Assert.Empty(Twitch.Settings.RequestedScopes); // empty means "everything"

        settings.RequestMinimalCommand.Execute(null);
        Assert.Equal(TwitchScopes.Minimal.Order(), Twitch.Settings.RequestedScopes.Order());

        settings.Scopes.Single(s => s.Scope == TwitchScopes.ManagePolls).IsRequested = true;
        Assert.Contains(TwitchScopes.ManagePolls, Twitch.Settings.RequestedScopes);
        Assert.Contains(TwitchScopes.ManagePolls, TwitchScopes.ForBroadcaster(Twitch.Settings.RequestedScopes));
        Assert.False(settings.Scopes.Single(s => s.Scope == TwitchScopes.ReadChat).CanChange);

        settings.RequestAllCommand.Execute(null);
        Assert.Empty(Twitch.Settings.RequestedScopes);

        settings.ViewerCountInterval = 120;
        Assert.Equal(120, Twitch.Settings.ViewerCountIntervalSeconds);
    }

    [AvaloniaFact]
    public void Accounts_tab_writes_bot_preferences()
    {
        var accounts = new TwitchViewModel(Twitch).Accounts;

        accounts.ClientId = "  my-client  ";
        accounts.SendChatAsBot = false;
        accounts.SendWhispersAsBot = false;

        Assert.Equal("my-client", Twitch.Settings.ClientId);
        Assert.False(Twitch.Settings.SendChatAsBot);
        Assert.False(Twitch.Settings.SendWhispersAsBot);
        Assert.Equal("No bot account", accounts.BotAccount);
        Assert.False(accounts.IsBotAuthorized);
    }

    [AvaloniaFact]
    public async Task Tabs_report_missing_sign_in_instead_of_throwing()
    {
        var vm = new TwitchViewModel(Twitch);

        await vm.Channel.LoadCommand.ExecuteAsync(null);
        Assert.Contains("Sign in", vm.Channel.Error);

        vm.Moderation.User = "someone";
        await vm.Moderation.BanCommand.ExecuteAsync(null);
        Assert.Contains("Sign in", vm.Moderation.Error);

        vm.Moderation.User = "";
        await vm.Moderation.TimeoutCommand.ExecuteAsync(null);
        Assert.Contains("user name", vm.Moderation.Error);

        await vm.Polls.ResolvePredictionCommand.ExecuteAsync(null);
        Assert.Contains("winning outcome", vm.Polls.Error);
    }

    [AvaloniaFact]
    public void Reward_items_turn_into_reward_updates()
    {
        var item = new TwitchRewardItem(new TwitchReward("r1", "Hydrate", "Drink", 500, "#00E5CB", true, false, true, false, false, 0, 2, 60, true))
        {
            Cost = 750,
            MaxPerStream = 3,
        };
        var update = item.ToUpdate();

        Assert.Equal(750, update.Cost);
        Assert.Equal(3, update.MaxPerStream);
        Assert.Equal(2, update.MaxPerUserPerStream);
        Assert.Equal(60, update.GlobalCooldownSeconds);
        Assert.Equal(false, update.IsPaused);
        Assert.False(item.IsReadOnly);
        Assert.Contains("750 points", item.Summary);
        Assert.Null(new TwitchRewardItem().ToUpdate().IsPaused);
        Assert.True(new TwitchRewardItem(new TwitchReward("x", "Theirs", "", 1, "", true, false, true, false, false, 0, 0, 0, false)).IsReadOnly);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
