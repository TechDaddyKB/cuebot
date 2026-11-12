using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using static Cuebot.UiTests.DocsScreenshots;

namespace Cuebot.UiTests;

/// <summary>
/// Documentation screenshots of a signed-in Cuebot: platform pages with a real channel, real rewards and connected
/// services. Run by hand against a data folder where someone signed in (never a streamer's everyday folder):
/// CUEBOT_SIGNED_IN_DATA=&lt;data folder&gt; CUEBOT_SCREENSHOT_OUT=&lt;empty folder&gt;. Every page, tab and card is saved
/// there to pick from. No demo data, no fake events and no actions are run, so nothing reaches the real channels;
/// connecting is the same as starting Cuebot. With CUEBOT_SAMPLE_REWARDS=1 a few disabled sample rewards are made on
/// Twitch and Kick for the reward pictures (viewers never see disabled rewards) and deleted again at the end.
/// </summary>
public sealed class SignedInScreenshots
{
    [AvaloniaFact]
    public void Write_the_signed_in_screenshots()
    {
        var data = Environment.GetEnvironmentVariable("CUEBOT_SIGNED_IN_DATA");
        var output = Environment.GetEnvironmentVariable("CUEBOT_SCREENSHOT_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(data) || string.IsNullOrEmpty(output),
            "Set CUEBOT_SIGNED_IN_DATA and CUEBOT_SCREENSHOT_OUT to take signed-in screenshots");
        Directory.CreateDirectory(output!);
        // Read the sign-ins the way the app does (Program.cs): the folder's and profiles' keys are in the desktop keyring.
        // With the tests' default (a key file) the store would not open, and Cuebot sets an unreadable store aside.
        var root = Path.GetFullPath(data!);
        var profilesRoot = Path.Combine(root, Cuebot.Core.Profiles.ProfileCatalog.FolderName) + Path.DirectorySeparatorChar;
        var protector = Cuebot.Core.Storage.SecretStore.ProtectorFor;
        Cuebot.Core.Storage.SecretStore.ProtectorFor = folder => folder == root || folder.StartsWith(profilesRoot, StringComparison.Ordinal)
            ? new Cuebot.App.Platform.SecretServiceKeyProtector(m => throw new InvalidOperationException("Keyring: " + m))
            : protector(folder);
        var docs = OutputDir;
        OutputDir = output!;
        var samples = new List<(Cuebot.Core.Rewards.IChannelRewards Platform, string Id)>();
        var profiles = Cuebot.Core.Profiles.ProfileCatalog.Open(data!);
        var host = AppHost.Create(new AppPaths(profiles.ActiveFolder, data!));
        try
        {
            var vm = new MainWindowViewModel(host, new Cuebot.App.Shell.DetachedShellHost(host.Settings, profiles));
            var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
            window.Show();
            Pump();

            Wait(Task.Run(host.StartAsync));
            for (var i = 0; i < 60; i++) Settle();   // about 20 s for the platforms and services to connect
            var vts = host.Get<Cuebot.Integrations.VTubeStudio.VTubeStudioService>();
            if (!vts.IsConnected) Wait(Task.Run(() => vts.ConnectAsync()));   // its saved token: no pop-up
            if (Environment.GetEnvironmentVariable("CUEBOT_SAMPLE_REWARDS") == "1") Wait(Task.Run(() => AddSamples(host, samples)));

            foreach (var page in vm.Pages)
            {
                if (page is TwitchViewModel twitch)
                {
                    Wait(twitch.Rewards.ReloadCommand.ExecuteAsync(null));
                    twitch.Rewards.Selected = twitch.Rewards.Rewards.FirstOrDefault(r => r.IsManageable) ?? twitch.Rewards.Rewards.FirstOrDefault();
                    Wait(twitch.PowerUps.ReloadCommand.ExecuteAsync(null));
                }
                else if (page is Cuebot.App.ViewModels.Kick.KickViewModel kick)
                {
                    Wait(kick.RewardEditor.ReloadCommand.ExecuteAsync(null));
                    kick.RewardEditor.Selected = kick.RewardEditor.Rewards.FirstOrDefault(r => r.MadeByCuebot) ?? kick.RewardEditor.Rewards.FirstOrDefault();
                }
            }
            Settle();

            var cards = new HashSet<string>();
            foreach (var page in vm.Pages)
            {
                vm.SelectedPage = page;
                Settle();
                Save(window, "page-" + Slug(page.Title));
                var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault()?.Items.OfType<TabItem>()
                    .Where(t => t.Header is string && t.IsVisible).ToList() ?? [];
                if (tabs.Count == 0) SaveCards(window, cards);
                foreach (var tab in tabs)
                {
                    tab.IsSelected = true;
                    Settle();
                    Save(window, "tab-" + FileSlug(page.Title) + "-" + FileSlug((string)tab.Header!));
                    SaveCards(window, cards);
                }
            }
            window.Close();
        }
        finally
        {
            foreach (var (platform, id) in samples)
            {
                try { Wait(platform.DeleteAsync(id)); }
                catch (Exception e) { Console.Error.WriteLine($"Could not delete the sample {platform.Name} reward {id}: {e.Message}"); }
            }
            OutputDir = docs;
            Cuebot.Core.Storage.SecretStore.ProtectorFor = protector;
            Wait(host.DisposeAsync().AsTask());   // saves refreshed sign-ins back to the folder
        }
    }

    /// <summary>Disabled sample rewards on each signed-in platform, remembered so they are deleted afterwards.</summary>
    private static async Task AddSamples(AppHost host, List<(Cuebot.Core.Rewards.IChannelRewards Platform, string Id)> made)
    {
        (string Title, string Text, int Cost, bool Input, int Cooldown)[] samples =
        [
            ("Hydrate!", "Make the streamer drink some water", 100, false, 0),
            ("Bonk", "Bonk the streamer", 250, false, 30),
            ("Song request", "Paste a song link", 500, true, 0),
            ("Posture check", "Sit up straight!", 300, false, 60),
        ];
        foreach (var platform in host.Get<IEnumerable<Cuebot.Core.Rewards.IChannelRewards>>().Where(p => p.Ready))
            foreach (var s in samples)
            {
                var reward = new Cuebot.Core.Rewards.ChannelReward("", s.Title, s.Text, s.Cost, "", Enabled: false, Paused: false, UserInput: s.Input,
                    SkipQueue: false, 0, 0, platform.Rules.HasCooldown ? s.Cooldown : 0, null);
                made.Add((platform, (await platform.CreateAsync(reward)).Id));
            }
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) Settle();
        task.GetAwaiter().GetResult();
    }
}
