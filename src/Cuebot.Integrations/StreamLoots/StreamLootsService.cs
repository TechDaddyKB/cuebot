using System.Net;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.StreamLoots;

/// <summary>
/// StreamLoots alerts over the server-sent-events stream behind the user's Alerts widget URL: chest purchases, gifts and
/// card redemptions become bot events. Reconnects when the stream ends or goes silent (StreamLoots sends a keep-alive
/// comment every 30 s).
/// </summary>
public sealed class StreamLootsService : IntegrationConnection
{
    /// <summary>Three missed keep-alives.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(95);

    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly Uri _baseUri;

    public StreamLootsService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new SseTransport(SseTransport.SharedClient, IdleTimeout), IntegrationsTimings.Default,
            StreamLootsProtocol.DefaultBase)
    {
    }

    internal StreamLootsService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        IntegrationsTimings timings, Uri baseUri) : base("StreamLoots", bus, log, transports, timings)
    {
        _settings = settings;
        _baseUri = baseUri;
    }

    public StreamLootsSettings Settings => _settings.Value.StreamLoots;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Kinds accepted by <see cref="SendTestEvent"/>.</summary>
    public IReadOnlyList<string> TestEventKinds => [.. StreamLootsProtocol.Samples.Keys];

    /// <summary>Raised when an alert was published (UI "last alert" line).</summary>
    public event Action? AlertReceived;

    /// <summary>Short description of the newest alert, or "" before the first one.</summary>
    public string LastAlert { get; private set; } = "";

    protected override string EventPrefix => StreamLootsEvents.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [AlertId, Settings.AlertsUrl.Trim()];

    /// <summary>The saved alert id masked ("Saved alert id: ••••••••89ab"), or what is wrong with the saved URL.</summary>
    public string MaskedSetting => StreamLootsProtocol.ParseAlertId(Settings.AlertsUrl) switch
    {
        { Id: { } id } => $"Saved alert id: {Secrets.Mask(id)}",
        _ when Settings.AlertsUrl.Trim().Length == 0 => "No Alerts widget URL saved",
        var (_, error) => error ?? "",
    };

    private string? AlertId =>StreamLootsProtocol.ParseAlertId(Settings.AlertsUrl).Id;

    protected override string? MissingSettings() => StreamLootsProtocol.ParseAlertId(Settings.AlertsUrl).Error;

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints() =>
        AlertId is { } id ? [new IntegrationEndpoint(StreamLootsProtocol.StreamUrl(_baseUri, id), _baseUri.Host)] : [];

    private protected override string? AuthRejection(Exception error) =>
        error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest }
            ? "StreamLoots did not accept the alert id. Copy the Alerts widget URL again and click Connect."
            : null;

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        // The stream opens for any id (StreamLoots does not validate it), so "connected" means the stream is open.
        MarkConnected("Connected to the StreamLoots alerts stream; waiting for alerts");
        while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) Handle(text);
    }

    /// <summary>Publishes one alert (the JSON of a stream message). Returns false for unusable messages.</summary>
    internal bool Handle(string json, bool isTest = false)
    {
        if (StreamLootsProtocol.Map(json, isTest) is not { } alert)
        {
            Log.Debug(Name, "Ignored a stream message that is not an alert");
            return false;
        }
        Publish(alert.Name, alert.Args);
        LastAlert = StreamLootsFeedAdapter.Describe(new BotEvent($"{EventPrefix}.{alert.Name}", alert.Args))?.Title ?? alert.Name;
        AlertReceived?.Invoke();
        return true;
    }

    /// <summary>
    /// Publishes a sample alert of <paramref name="kind"/> locally (never sent to StreamLoots), marked <c>isTest</c>, so
    /// triggers and overlays can be tried. Returns false for an unknown kind.
    /// </summary>
    public bool SendTestEvent(string kind) =>
        StreamLootsProtocol.Samples.TryGetValue(kind.Trim(), out var sample) && Handle(sample, isTest: true);
}
