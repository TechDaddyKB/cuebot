using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.SocketIo;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.StreamElements;

/// <summary>
/// The StreamElements connection: real-time activities (tips, merch, store redemptions and, optionally, relayed
/// platform events) over the Astro gateway, or the legacy realtime Socket.IO endpoint as a fallback, plus the
/// REST API for points and tips. Authenticated with the channel's JWT token.
/// </summary>
public sealed class StreamElementsService : DonationServiceBase
{
    public static readonly Uri AstroEndpoint = new("wss://astro.streamelements.com");
    public static readonly Uri RealtimeEndpoint = new("https://realtime.streamelements.com");
    public const string ActivitiesTopic = "channel.activities";

    private readonly Func<IWebSocketTransport> _transports;
    private readonly Uri _astro;
    private readonly Uri _realtime;
    private readonly DonationsTimings _timings;
    private readonly StreamElementsApi _api;

    public StreamElementsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, HttpClient http)
        : this(settings, bus, log, () => new ClientWebSocketTransport(DonationsTimings.Default.KeepAlive), AstroEndpoint, RealtimeEndpoint,
            DonationsTimings.Default, new StreamElementsApi(http))
    {
    }

    internal StreamElementsService(JsonFile<DonationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        Uri astro, Uri realtime, DonationsTimings timings, StreamElementsApi api) : base(settings, bus, log)
    {
        _transports = transports;
        _astro = astro;
        _realtime = realtime;
        _timings = timings;
        _api = api;
    }

    public override string Name => StreamElementsEventMapper.Service;
    public StreamElementsSettings Settings => SettingsFile.Value.StreamElements;
    protected override string Token => Settings.JwtToken;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override bool RelayPlatformEvents => Settings.RelayPlatformEvents;
    protected override string ConnectedEvent => StreamElementsEventTypes.Connected;
    protected override string DisconnectedEvent => StreamElementsEventTypes.Disconnected;
    public override IReadOnlyList<string> TestEventKinds => [.. StreamElementsEventMapper.Samples.Keys];

    public override bool SendTestEvent(string kind)
    {
        if (!StreamElementsEventMapper.Samples.TryGetValue(kind, out var sample)) return false;
        using var doc = JsonDocument.Parse(sample);
        return StreamElementsEventMapper.Map(doc.RootElement, isTest: true) is { } e && Publish(e);
    }

    /// <summary>Handles one activity (also used by tests).</summary>
    internal bool HandleActivity(JsonElement activity) => StreamElementsEventMapper.Map(activity) is { } e && Publish(e);

    /// <summary>Handles one legacy <c>event:test</c> (the dashboard's emulated alerts), published as a test.</summary>
    internal bool HandleWidgetTest(JsonElement test) => StreamElementsEventMapper.MapWidgetTest(test) is { } e && Publish(e);

    // ---- REST API (sub-actions and script functions) ----

    /// <summary>The token's channel id, looked up once and cached in donations.json.</summary>
    public async Task<string> GetChannelIdAsync(CancellationToken ct = default)
    {
        if (Settings.ChannelId.Length > 0) return Settings.ChannelId;
        var channel = await _api.GetMyChannelAsync(Settings.JwtToken, ct).ConfigureAwait(false);
        Settings.ChannelId = channel.Id;
        Settings.ChannelName = channel.DisplayName;
        SaveSettings();
        return channel.Id;
    }

    /// <summary>Forgets the cached channel (after the token changes).</summary>
    public void ResetChannel()
    {
        Settings.ChannelId = "";
        Settings.ChannelName = "";
        SaveSettings();
    }

    public async Task<StreamElementsPoints> GetUserPointsAsync(string user, CancellationToken ct = default) =>
        await _api.GetUserPointsAsync(Settings.JwtToken, await GetChannelIdAsync(ct).ConfigureAwait(false), User(user), ct).ConfigureAwait(false);

    public async Task<long> AddUserPointsAsync(string user, long amount, CancellationToken ct = default) =>
        await _api.AddUserPointsAsync(Settings.JwtToken, await GetChannelIdAsync(ct).ConfigureAwait(false), User(user), amount, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsRank>> GetPointsLeaderboardAsync(int limit, CancellationToken ct = default) =>
        await _api.GetPointsLeaderboardAsync(Settings.JwtToken, await GetChannelIdAsync(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsTip>> GetTipsAsync(int limit, CancellationToken ct = default) =>
        await _api.GetTipsAsync(Settings.JwtToken, await GetChannelIdAsync(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsRank>> GetTipLeaderboardAsync(int limit, CancellationToken ct = default) =>
        await _api.GetTipLeaderboardAsync(Settings.JwtToken, await GetChannelIdAsync(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);

    private static string User(string user)
    {
        user = user.Trim().TrimStart('@');
        if (user.Length == 0) throw new ArgumentException("A user name is required");
        return user;
    }

    // ---- connection ----

    /// <summary>Validates the token through the REST API; a network failure is only logged (the socket retries).</summary>
    private protected override async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            ResetChannel();
            await GetChannelIdAsync(ct).ConfigureAwait(false);
        }
        catch (StreamElementsApiException ex) when (ex.Status is 401 or 403)
        {
            throw new InvalidOperationException("StreamElements rejected the JWT token. Copy it again from Account → Channels → Show secrets.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StreamElementsApiException)
        {
            Log.Warn(Name, $"Could not look up the channel: {Secrets.Redact(ex.Message, Token)}");
        }
    }

    private protected override Task RunAsync(CancellationToken ct) =>
        Settings.Transport == StreamElementsTransport.LegacyRealtime ? RunRealtimeAsync(ct) : RunAstroAsync(ct);

    private async Task RunAstroAsync(CancellationToken ct)
    {
        var token = Settings.JwtToken.Trim();
        var client = new AstroClient(_transports, _astro, _timings, Log);
        client.Connected += () => SetStatus(ConnectionState.Connecting, "Subscribing to StreamElements activities...");
        client.Subscribed += _ => OnConnected($"Connected to StreamElements{ChannelSuffix()} (Astro)");
        client.SubscriptionFailed += (topic, error, description) =>
            SetStatus(ConnectionState.Error, $"Could not subscribe to {topic}: {error} {description}".TrimEnd());
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("rejected", StringComparison.Ordinal));
        client.MessageReceived += (topic, data) =>
        {
            if (topic == ActivitiesTopic) HandleActivity(data);
        };
        await client.RunAsync(token, [ActivitiesTopic], ct).ConfigureAwait(false);
    }

    private async Task RunRealtimeAsync(CancellationToken ct)
    {
        var token = Settings.JwtToken.Trim();
        var client = new SocketIoClient(_transports, _timings, Log, Name)
        {
            EngineIoVersion = 3,
            Redact = text => Secrets.Redact(text, token),
            OnConnectedAsync = (send, _) => send(SocketIoFraming.Event("authenticate", new { method = "jwt", token })),
        };
        client.Connected += _ => SetStatus(ConnectionState.Connecting, "Authenticating with StreamElements...");
        client.Disconnected += reason => OnDisconnected(reason, fatal: reason.Contains("refused", StringComparison.Ordinal));
        client.EventReceived += (name, args) =>
        {
            switch (name)
            {
                case "authenticated":
                    OnConnected($"Connected to StreamElements{ChannelSuffix()} (legacy realtime)");
                    break;
                case "unauthorized":
                    SetStatus(ConnectionState.Error, "StreamElements rejected the JWT token");
                    break;
                case "event" when args.Count > 0:
                    HandleActivity(args[0]);
                    break;
                case "event:test" when args.Count > 0:
                    HandleWidgetTest(args[0]);
                    break;
            }
        };
        await client.RunAsync(eio => SocketIoFraming.Url(_realtime, eio, new Dictionary<string, string>()), ct).ConfigureAwait(false);
    }

    private string ChannelSuffix() => Settings.ChannelName.Length > 0 ? $" as {Settings.ChannelName}" : "";
}
