using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
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
}
