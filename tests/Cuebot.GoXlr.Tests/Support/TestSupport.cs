using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.GoXlr.Protocol;

namespace Cuebot.GoXlr.Tests.Support;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }
}

/// <summary>A service wired to a fake utility, with every published event recorded.</summary>
internal sealed class TestEnvironment : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-goxlr-").FullName;

    public TestEnvironment()
    {
        Log = new AppLog();
        Bus = new EventBus(Log);
        Settings = new JsonFile<GoXlrSettings>(Path.Combine(_dir, "goxlr.json"), Log);
        Bus.Subscribe(e => Events.Enqueue(e));
    }

    public AppLog Log { get; }
    public EventBus Bus { get; }
    public JsonFile<GoXlrSettings> Settings { get; }
    public ConcurrentQueue<BotEvent> Events { get; } = new();

    public static readonly GoXlrTimings Fast = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

    public GoXlrService Service(FakeGoXlrUtility? fake = null, Func<IGoXlrTransport>? transports = null)
    {
        if (fake is not null)
        {
            Settings.Value.Host = "localhost";
            Settings.Value.Port = fake.Port;
        }
        return new GoXlrService(Settings, Bus, Log, transports ?? (() => new ClientWebSocketTransport()), Fast);
    }

    public async Task<BotEvent> NextAsync(string type, Func<BotEvent, bool>? match = null)
    {
        BotEvent? found = null;
        await Wait.UntilAsync(() => (found = Events.FirstOrDefault(e => e.Type == type && (match?.Invoke(e) ?? true))) is not null, type);
        return found!;
    }

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
