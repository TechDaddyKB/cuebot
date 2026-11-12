using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Views;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.UiTests.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests;

/// <summary>The sidebar lists only connections in use; the Dashboard keeps all of them.</summary>
public class SidebarConnectionsTests
{
    private sealed class FakeConnection(string name) : IConnectionStatus
    {
        public string Name => name;
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public string StatusMessage => State.ToString();
        public event Action? StatusChanged;

        public void Set(ConnectionState state)
        {
            State = state;
            StatusChanged?.Invoke();
        }
    }

    [AvaloniaFact]
    public async Task Unused_connections_stay_out_of_the_sidebar_until_they_are_used()
    {
        var twitch = new FakeConnection("Twitch");
        var meld = new FakeConnection("Meld Studio");
        await using var services = new ServiceCollection()
            .AddCuebotCore(new AppPaths(Path.Combine(Path.GetTempPath(), "cuebot-sidebar-" + Guid.NewGuid().ToString("N"))))
            .BuildServiceProvider();
        var dashboard = new DashboardViewModel(services.GetRequiredService<AppLog>(), services.GetRequiredService<ActionEngine>(), [twitch, meld]);

        Assert.Equal(2, dashboard.Connections.Count);
        Assert.Empty(dashboard.SidebarConnections);
        Assert.False(dashboard.HasSidebarConnections);

        twitch.Set(ConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Twitch"], dashboard.SidebarConnections.Select(c => c.Name));
        Assert.True(dashboard.HasSidebarConnections);

        // A connection that drops stays listed (grey), so the user sees it went away.
        twitch.Set(ConnectionState.Disconnected);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Twitch"], dashboard.SidebarConnections.Select(c => c.Name));

        meld.Set(ConnectionState.Error);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Twitch", "Meld Studio"], dashboard.SidebarConnections.Select(c => c.Name));
    }

    [AvaloniaFact]
    public async Task Many_connections_become_small_badges_that_never_cover_the_title_or_the_search_box()
    {
        string[] names = ["Twitch", "YouTube", "Kick", "Rumble", "Streamlabs", "StreamElements", "VTube Studio", "Mod portal relay", "OBS"];
        var fakes = names.Select(n => new FakeConnection(n)).ToArray();
        var data = Directory.CreateTempSubdirectory("cuebot-chips-").FullName;
        await using var host = AppHost.Create(new AppPaths(data), overrides: s =>
        {
            foreach (var f in fakes) s.AddSingleton<IConnectionStatus>(f);
        });
        var vm = new MainWindowViewModel(host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        foreach (var f in fakes) f.Set(ConnectionState.Connected);
        DocsScreenshots.Settle();

        Assert.True(vm.Dashboard.CompactChips);
        var compact = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "CompactChips");
        Assert.True(compact.IsEffectivelyVisible);
        Assert.False(window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "Chips").IsEffectivelyVisible);
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var title = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TddMark");
        var jump = window.GetVisualDescendants().OfType<AutoCompleteBox>().Single(b => b.Name == "Jump");
        Assert.True(Left(title) + title.Bounds.Width <= Left(jump), "the search box covers the title");
        Assert.True(Left(jump) + jump.Bounds.Width <= Left(compact), "the badges cover the search box");
        UiPump.Save(window, "topbar-many-connections");
        window.Close();
    }
}
