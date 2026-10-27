using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.TipeeeStream;

/// <summary>
/// The TipeeeStream real-time connection: the socket server comes from <c>GET /v2.0/site/socket</c>, then Socket.IO v2
/// (Engine.IO 3) with <c>access_token=&lt;API key&gt;</c>, <c>join-room</c> and <c>new-event</c> pushes. Donations
/// and, optionally, the platform events TipeeeStream relays.
/// </summary>
public sealed class TipeeeStreamService : DonationServiceBase
{
    private readonly Func<IWebSocketTransport> _transports;
    private readonly DonationsTimings _timings;
    private readonly TipeeeStreamApi _api;
    private Uri _server = TipeeeStreamApi.FallbackSocketServer;

    public TipeeeStreamService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), DonationsTimings.Default,
            new TipeeeStreamApi(http))
    {
    }

    internal TipeeeStreamService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        DonationsTimings timings, TipeeeStreamApi api) : base(settings, bus, log)
    {
        _transports = transports;
        _timings = timings;
        _api = api;
    }

    public override string Name => TipeeeStreamEventMapper.Service;
    public TipeeeStreamSettings Settings => SettingsFile.Value.TipeeeStream;
    protected override string Token => Settings.ApiKey;
    protected override string MissingTokenMessage => "Enter your TipeeeStream API key first.";
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => Settings.RelayPlatformEvents;
    protected override string ConnectedEvent => TipeeeStreamEventTypes.Connected;
    protected override string DisconnectedEvent => TipeeeStreamEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. TipeeeStreamEventMapper.Samples.Keys];

    /// <summary>The socket server in use (from the lookup, or the fallback).</summary>
    public Uri SocketServer => _server;

    public override bool SendTestEvent(string kind)
    {
        if (!TipeeeStreamEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        return TipeeeStreamEventMapper.Map(doc.RootElement, isTest: true) is { } e && Publish(e);
    }

    /// <summary>Handles one <c>new-event</c> argument (also used by tests).</summary>
    internal bool HandleEvent(JsonElement payload) => TipeeeStreamEventMapper.Map(payload) is { } e && Publish(e);

    /// <summary>Checks the key and looks up the socket server; network failures are only logged (the socket retries).</summary>
    private protected override async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            await _api.ValidateKeyAsync(Settings.ApiKey, ct).ConfigureAwait(false);
        }
        catch (TipeeeStreamApiException ex) when (ex.Status is 401 or 403)
        {
            throw new InvalidOperationException("TipeeeStream rejected the API key. Copy it again from your TipeeeStream account (API).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TipeeeStreamApiException)
        {
            Log.Warn(Name, $"Could not check the API key: {RedactSecrets(ex.Message)}");
        }
        try
        {
            _server = await _api.GetSocketServerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TipeeeStreamApiException)
        {
            _server = TipeeeStreamApi.FallbackSocketServer;
            Log.Warn(Name, $"Socket server lookup failed ({ex.Message}); using {_server.Host}");
        }
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        var key = Settings.ApiKey.Trim();
        var username = Settings.Username.Trim();
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = 3,
            Redact = text => Secrets.Redact(text, key),
            OnConnectedAsync = (send, _) => send(SocketIoFraming.Event("join-room",
                new { room = key, username = username.Length > 0 ? username : "cuebot" })),
        };
        client.Connected += eio => OnConnected($"Connected to TipeeeStream (Socket.IO {(eio >= 4 ? "v4" : "v2")})");
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("refused", StringComparison.Ordinal));
        client.EventReceived += (name, args) =>
        {
            if (name == "new-event" && args.Count > 0) HandleEvent(args[0]);
        };
        await client.RunAsync(eio => SocketIoFraming.Url(_server, eio, new Dictionary<string, string> { ["access_token"] = key }), ct)
            .ConfigureAwait(false);
    }
}
