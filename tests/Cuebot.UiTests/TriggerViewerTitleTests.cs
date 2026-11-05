using Avalonia.Headless.XUnit;
using Cuebot.App;
using Cuebot.App.ViewModels.History;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.UiTests;

/// <summary>The Dashboard and Trigger viewer name each event after the trigger that belongs to it.</summary>
public sealed class TriggerViewerTitleTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-viewer-").FullName;
    private readonly AppHost _host;

    public TriggerViewerTitleTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    [AvaloniaTheory]
    [InlineData("twitch.chatMessage", "Twitch › Chat message")]
    [InlineData("users.firstWords", "Users › First words")]
    public void An_event_is_named_after_its_own_trigger_not_another_listener(string eventType, string title)
    {
        var viewer = new TriggerViewerViewModel(new EventBus(new AppLog()), _host.Get<Registry>());

        viewer.Add(new BotEvent(eventType, new Dictionary<string, object?> { ["platform"] = "Twitch", ["user"] = "Ann" }));

        Assert.Equal(title, Assert.Single(viewer.Events).Title);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
