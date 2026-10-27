using System.Collections.Concurrent;
using System.Text;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;

namespace Cuebot.Webhooks.Tests.Support;

/// <summary>A temp settings file, an event bus that records what is published, and delivery builders.</summary>
public sealed class Harness : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-webhooks-tests-" + Guid.NewGuid().ToString("N"));

    public Harness()
    {
        Directory.CreateDirectory(_dir);
        Settings = new JsonFile<WebhooksSettings>(Path.Combine(_dir, "webhooks.json"), Log);
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<WebhooksSettings> Settings { get; }
    public ConcurrentQueue<BotEvent> Published { get; } = new();

    public BotEvent[] Events(string type) => Published.Where(e => e.Type == type).ToArray();

    public static string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    public static WebhookDelivery Delivery(string service, string body, IReadOnlyDictionary<string, string>? headers = null,
        string path = "", string query = "", string? id = null, string method = "POST") =>
        new(id ?? Guid.NewGuid().ToString("N"), service, path, method, query,
            (headers ?? new Dictionary<string, string>()).ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value),
            Encoding.UTF8.GetBytes(body), DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
