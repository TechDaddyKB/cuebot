using System.Collections.Concurrent;
using System.Text;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

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

/// <summary>A locally generated Ed25519 key pair standing in for Throne's signing key.</summary>
public sealed class ThroneSigner
{
    private readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());

    public byte[] PublicKey => _key.GeneratePublicKey().GetEncoded();

    /// <summary>Hex Ed25519 signature of <c>timestamp "." body</c>, as Throne sends in X-Signature-Ed25519.</summary>
    public string Sign(string timestamp, string body)
    {
        var message = Encoding.UTF8.GetBytes(timestamp + "." + body);
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToHexStringLower(signer.GenerateSignature());
    }

    public static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A Throne delivery signed with this key (timestamp: now unless given); <paramref name="extra"/> headers are added.</summary>
    public WebhookDelivery Delivery(string body, string? timestamp = null, string path = "", string query = "", string? id = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var ts = timestamp ?? Now();
        var headers = new Dictionary<string, string>(extra ?? new Dictionary<string, string>())
        {
            ["X-Signature-Timestamp"] = ts,
            ["X-Signature-Ed25519"] = Sign(ts, body),
        };
        return Harness.Delivery("throne", body, headers, path, query, id);
    }
}
