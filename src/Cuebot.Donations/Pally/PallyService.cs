using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.Pally;

/// <summary>
/// The Pally.gg event websocket (wss://events.pally.gg, authenticated with an API key in the query): campaign tips for
/// every page the user owns (<c>channel=firehose</c>) or for one page (<c>channel=activity-feed&amp;room=&lt;slug&gt;</c>).
/// </summary>
public sealed class PallyService : DonationServiceBase
{
    public static readonly Uri Production = new("wss://events.pally.gg");

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _endpoint;
    private readonly DonationsTimings _timings;
    private readonly RecentIds _tipIds = new();
    private PallyClient? _client;

    public PallyService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), Production, DonationsTimings.Default)
    {
    }

    internal PallyService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri endpoint, DonationsTimings timings) : base(settings, bus, log)
    {
        _transports = transports;
        _endpoint = endpoint;
        _timings = timings;
    }

    public override string Name => PallyEventMapper.Service;
    public PallySettings Settings => SettingsFile.Value.Pally;
    protected override string Token => Settings.ApiKey;
    protected override string MissingTokenMessage => "Enter your Pally.gg API key first.";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => false;
    protected override string ConnectedEvent => PallyEventTypes.Connected;
    protected override string DisconnectedEvent => PallyEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => PallyEventMapper.TestKinds;

    public override bool SendTestEvent(string kind)
    {
        if (!PallyEventMapper.TestKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) return false;
        using var doc = JsonDocument.Parse(PallyEventMapper.Sample("test"));
        return PallyEventMapper.Map(doc.RootElement, isTest: true) is { } e && Publish(e);
    }

    /// <summary>
    /// Asks Pally's server to echo a sample tip back on this connection, which tests the whole path (Pally → Cuebot →
    /// triggers). The tip arrives marked <c>isTest</c>/<c>isEcho</c>. False when not connected.
    /// </summary>
    public async Task<bool> SendEchoTestAsync(CancellationToken ct = default)
    {
        if (_client is not { } client || State != ConnectionState.Connected) return false;
        var sample = PallyEventMapper.Sample(PallyEventMapper.EchoPrefix + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(PallyEventMapper.Echo(sample), ct).ConfigureAwait(false);
    }

    /// <summary>Handles one message (also used by tests).</summary>
    internal bool HandleMessage(JsonElement message)
    {
        if (PallyEventMapper.Map(message) is not { } e) return false;
        // A tip seen before is a "Replay Alert" from the activity feed: published again, marked isReplay.
        e.Args["isReplay"] = e.Args["tipId"] is string { Length: > 0 } id && !_tipIds.Add(id);
        return Publish(e);
    }

    /// <summary>The websocket URL (contains the key: never log it).</summary>
    internal Uri Url()
    {
        var key = Uri.EscapeDataString(Settings.ApiKey.Trim());
        var slug = Settings.PageSlug.Trim().Trim('/');
        var query = slug.Length > 0 ? $"auth={key}&channel=activity-feed&room={Uri.EscapeDataString(slug)}" : $"auth={key}&channel=firehose";
        return new UriBuilder(_endpoint) { Query = query }.Uri;
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var client = new PallyClient(_transports, _timings, Log);
        var slug = Settings.PageSlug.Trim();
        client.Connected += () => OnConnected(slug.Length > 0 ? $"Connected to Pally.gg (page {slug})" : "Connected to Pally.gg (all your pages)");
        client.Disconnected += (reason, fatal) => OnDisconnected(reason, fatal);
        client.MessageReceived += message => HandleMessage(message);
        _client = client;
        try
        {
            await client.RunAsync(Url(), RedactSecrets, ct).ConfigureAwait(false);
        }
        finally
        {
            _client = null;
        }
    }
}
