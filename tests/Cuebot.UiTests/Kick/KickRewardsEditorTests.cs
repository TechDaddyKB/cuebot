using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels.Kick;
using Cuebot.Core;
using Cuebot.Kick;
using Cuebot.Kick.Api;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Kick;

/// <summary>The Kick page's rewards editor: renders in both themes, and counts rewards against Kick's 50.</summary>
public sealed class KickRewardsEditorTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-kickrewards-").FullName;
    private readonly AppHost _host;

    public KickRewardsEditorTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    [AvaloniaFact]
    public void The_editor_renders_a_new_reward_without_binding_errors_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new KickViewModel(_host.Get<KickService>());
            vm.RewardEditor.NewCommand.Execute(null);
            var window = new Window
            {
                Content = new Cuebot.App.Views.Kick.KickView { DataContext = vm },
                Width = 1300,
                Height = 2600,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"kick-rewards-{name}");
            }
            var editor = window.GetVisualDescendants().OfType<Cuebot.App.Views.Kick.KickRewardsView>().Single();
            Assert.Equal("Kick allows up to 50 rewards", editor.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "UsedText").Text);
            Assert.Equal("New reward", editor.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RewardTitle").Text);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void New_rewards_stop_at_kicks_limit_and_a_copy_keeps_the_settings()
    {
        var editor = new KickRewardsEditorViewModel(_host.Get<KickService>());
        for (var i = 0; i < KickChannelRewards.MaxRewards; i++) editor.NewCommand.Execute(null);
        Assert.False(editor.CanAdd);
        editor.NewCommand.Execute(null);
        Assert.Equal(KickChannelRewards.MaxRewards, editor.Rewards.Count);
        Assert.Contains("50 rewards per channel", editor.Error);

        var item = new KickRewardItem(new KickReward("01A", "Hydrate", "Drink", 300, "#123456", true, true, true, false), madeByCuebot: false);
        Assert.True(item.MaybeReadOnly);
        Assert.Contains("made outside Cuebot", item.Summary);
        var update = item.ToUpdate();
        Assert.Equal(("Hydrate", "Drink", 300, true, true), (update.Title, update.Description, update.Cost, update.IsPaused, update.IsUserInputRequired));
        var copy = item.Duplicate();
        Assert.Equal(("Hydrate (copy)", 300m, true), (copy.Title, copy.Cost, copy.IsNew));
        Assert.Null(copy.ToUpdate().IsPaused);   // Kick creates rewards unpaused
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
