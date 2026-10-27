using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.Streamlabs;

/// <summary>
/// The Streamlabs Socket API connection (Socket.IO at sockets.streamlabs.com, authenticated with the Socket API
/// token in the query string): donations, charity donations, merch, loyalty store redemptions, Prime gifts and,
/// optionally, the platform events Streamlabs relays.
/// </summary>
public sealed class StreamlabsService : DonationServiceBase
{
    public static readonly Uri Production = new("https://sockets.streamlabs.com");

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _server;
    private readonly DonationsTimings _timings;

    public StreamlabsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), Production, DonationsTimings.Default)
    {
    }

    internal StreamlabsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri server, DonationsTimings timings) : base(settings, bus, log)
    {
        _transports = transports;
        _server = server;
        _timings = timings;
    }

    public override string Name => StreamlabsEventMapper.Service;
    public StreamlabsSettings Settings => SettingsFile.Value.Streamlabs;
    protected override string Token => Settings.SocketToken;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => Settings.RelayPlatformEvents;
    protected override string ConnectedEvent => StreamlabsEventTypes.Connected;
    protected override string DisconnectedEvent => StreamlabsEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. StreamlabsEventMapper.Samples.Keys];

    public override bool SendTestEvent(string kind)
    {
        if (!StreamlabsEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        foreach (var e in StreamlabsEventMapper.Map(doc.RootElement, isTest: true)) Publish(e);
        return true;
    }

    /// <summary>Handles the payload of one Socket.IO "event" (also used by tests).</summary>
    internal int HandleEvent(JsonElement payload) => StreamlabsEventMapper.Map(payload).Count(Publish);

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var token = Settings.SocketToken.Trim();
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = Settings.EngineIoVersion is 3 ? 3 : 4,
            Redact = text => Secrets.Redact(text, token),
        };
        client.Connected += eio =>
        {
            if (Settings.EngineIoVersion != eio)
            {
                Settings.EngineIoVersion = eio;
                SaveSettings();
            }
            OnConnected($"Connected to the Streamlabs Socket API (Socket.IO {(eio >= 4 ? "v4" : "v2")})");
        };
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("refused", StringComparison.Ordinal));
        client.EventReceived += (name, args) =>
        {
            if (name == "event" && args.Count > 0) HandleEvent(args[0]);
        };
        await client.RunAsync(eio => SocketIoFraming.Url(_server, eio, new Dictionary<string, string> { ["token"] = token }), ct)
            .ConfigureAwait(false);
    }
}
