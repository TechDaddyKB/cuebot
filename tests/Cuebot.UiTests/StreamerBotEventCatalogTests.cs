using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Scripting.Compat;

namespace Cuebot.UiTests;

/// <summary>
/// Checks the Streamer.bot WebSocket event names against the real composition root: every event type a loaded module
/// can raise maps to a name that GetEvents advertises.
/// </summary>
public sealed class StreamerBotEventCatalogTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-sbmap-").FullName;
    private readonly AppHost _host;

    public StreamerBotEventCatalogTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    [Fact]
    public void Dump()
    {
        var path = Environment.GetEnvironmentVariable("CUEBOT_DUMP_EVENTS");
        if (string.IsNullOrEmpty(path)) return;
        var registry = _host.Get<Registry>();
        var lines = registry.Triggers
            .SelectMany(t => t.EventTypes.Select(e => $"{e}\t{StreamerBotEventMap.Map(e)}\t{t.Category}\t{t.DisplayName}\t{t.TypeId}"))
            .Order(StringComparer.Ordinal);
        File.WriteAllLines(path, lines);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
