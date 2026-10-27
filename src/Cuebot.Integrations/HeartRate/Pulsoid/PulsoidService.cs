using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.HeartRate.Pulsoid;

/// <summary>Pulsoid realtime frames (v2 JSON, v1 JSON and the plain-number response mode).</summary>
internal static class PulsoidProtocol
{
    public static readonly Uri DefaultBase = new("wss://dev.pulsoid.net/api/");

    public static Uri Endpoint(Uri baseUri, bool legacyV1) =>
        new(baseUri, legacyV1 ? "v1/data/real_time" : "v2/data/real_time?types=heart_rate");

    /// <summary>The BPM in a frame, or null for other frame types (battery_level, ingestion_pause...).</summary>
    public static int? ParseBpm(string text)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;
        if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } root) return null;
        var type = root.Str("type");
        if (type.Length > 0)
            return type == "heart_rate" && root.Obj("heartRate").Num("bpm") is { } bpm ? (int)Math.Round(bpm) : null;
        return root.Obj("data").Num("heart_rate") is { } v1 ? (int)Math.Round(v1) : null;
    }
}

/// <summary>
/// Pulsoid realtime heart rate (wss://dev.pulsoid.net/api/v2/data/real_time) with the user's access token sent as a
/// Bearer header (never in the URL). Reconnects with backoff; stops on 401/403 (bad token) or 400 (missing scope).
/// </summary>
public sealed class PulsoidService : HeartRateConnection
{
    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly Uri _baseUri;

    public PulsoidService(JsonFile<IntegrationsSettings> settings, HeartRateHub hub, EventBus bus, AppLog log)
        : this(settings, hub, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default, PulsoidProtocol.DefaultBase)
    {
    }

    internal PulsoidService(JsonFile<IntegrationsSettings> settings, HeartRateHub hub, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, IntegrationsTimings timings, Uri baseUri)
        : base(HeartRateHub.PulsoidSource, hub, bus, log, transports, timings)
    {
        _settings = settings;
        _baseUri = baseUri;
    }

    public PulsoidSettings Settings => _settings.Value.Pulsoid;

    public void SaveSettings() => _settings.ScheduleSave();

    protected override string EventPrefix => HeartRateEventTypes.Pulsoid.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [Settings.AccessToken.Trim(), Settings.AccessToken];

    protected override string? MissingSettings() =>
        Settings.AccessToken.Trim().Length == 0 ? "Enter your Pulsoid access token first (pulsoid.net → Account → Tokens)." : null;

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints()
    {
        var url = PulsoidProtocol.Endpoint(_baseUri, Settings.UseLegacyV1);
        return [new IntegrationEndpoint(url, $"{url.Host} ({(Settings.UseLegacyV1 ? "v1" : "v2")})",
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + Settings.AccessToken.Trim() })];
    }

    private protected override string? AuthRejection(Exception error) => UpgradeStatus(error) switch
    {
        401 or 403 => "Pulsoid rejected the access token (expired or revoked?). Paste a new one and click Connect.",
        400 => "Pulsoid refused the connection: the token needs the data:heart_rate:read scope.",
        _ => null,
    };

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        MarkConnected("Connected to Pulsoid; waiting for heart rate data");
        while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
        {
            if (PulsoidProtocol.ParseBpm(text) is { } bpm)
            {
                Report(bpm);
                SetStatus(Cuebot.Core.ConnectionState.Connected, $"Connected to Pulsoid: {bpm} BPM");
            }
        }
    }
}
