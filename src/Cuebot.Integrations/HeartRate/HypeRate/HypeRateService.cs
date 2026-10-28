using System.Globalization;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.HeartRate.HypeRate;

/// <summary>One Phoenix channels (V1 JSON serializer) message.</summary>
internal sealed record PhoenixMessage(string Topic, string Event, JsonElement Payload, string? Ref);

/// <summary>Phoenix channel framing as used by HypeRate (see PROTOCOL.md).</summary>
internal static class PhoenixProtocol
{
    public static readonly Uri DefaultBase = new("wss://app.hyperate.io/socket/websocket");

    public static string Message(string topic, string @event, int reference, object? payload = null) =>
        JsonUtil.Serialize(new { topic, @event, payload = payload ?? new { }, @ref = reference });

    public static string Join(string topic, int reference) => Message(topic, "phx_join", reference);

    public static string Heartbeat(int reference) => Message("phoenix", "heartbeat", reference);

    public static PhoenixMessage? Parse(string text)
    {
        if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } root) return null;
        var reference = root.Str("ref");
        return new PhoenixMessage(root.Str("topic"), root.Str("event"), root.Obj("payload"), reference.Length > 0 ? reference : null);
    }

    /// <summary>"ok" / "error" of a phx_reply.</summary>
    public static string ReplyStatus(PhoenixMessage reply) => reply.Payload.Str("status");

    public static string ReplyReason(PhoenixMessage reply) =>
        reply.Payload.Obj("response").Str("reason") is { Length: > 0 } reason ? reason : reply.Payload.Obj("response").GetRawTextOrEmpty();

    private static string GetRawTextOrEmpty(this JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "" : e.GetRawText();

    public static Uri Endpoint(Uri baseUri, string apiKey) =>
        new($"{baseUri.GetLeftPart(UriPartial.Path)}?token={Uri.EscapeDataString(apiKey)}");
}

/// <summary>
/// HypeRate heart rate over its Phoenix-channels websocket: joins "hr:&lt;session id&gt;" (and "clips:&lt;id&gt;"),
/// sends a heartbeat every 10 s, reconnects when the socket, a heartbeat or the channel fails.
/// </summary>
public sealed class HypeRateService : HeartRateConnection
{
    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly Uri _baseUri;
    private int _ref;

    public HypeRateService(JsonFile<IntegrationsSettings> settings, HeartRateHub hub, EventBus bus, AppLog log)
        : this(settings, hub, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default, PhoenixProtocol.DefaultBase)
    {
    }

    internal HypeRateService(JsonFile<IntegrationsSettings> settings, HeartRateHub hub, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, IntegrationsTimings timings, Uri baseUri)
        : base(HeartRateHub.HypeRateSource, hub, bus, log, transports, timings)
    {
        _settings = settings;
        _baseUri = baseUri;
    }

    public HypeRateSettings Settings => _settings.Value.HypeRate;

    public void SaveSettings() => _settings.ScheduleSave();

    protected override string EventPrefix => HeartRateEventTypes.HypeRate.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [Settings.ApiKey.Trim(), Settings.ApiKey];

    private string SessionId => Settings.SessionId.Trim();

    protected override string? MissingSettings() =>
        Settings.ApiKey.Trim().Length == 0 ? "Enter your HypeRate API key first."
        : SessionId.Length == 0 ? "Enter your HypeRate ID (shown in the HypeRate app) first."
        : null;

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints() =>
        [new IntegrationEndpoint(PhoenixProtocol.Endpoint(_baseUri, Settings.ApiKey.Trim()), _baseUri.Host)];

    private protected override string? AuthRejection(Exception error) => UpgradeStatus(error) switch
    {
        401 or 403 => "HypeRate rejected the API key. Check it and click Connect.",
        _ => null,
    };

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var id = SessionId;
        var hrTopic = "hr:" + id;
        var clipsTopic = "clips:" + id;
        var joinRef = Next();
        var joined = new TaskCompletionSource<PhoenixMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatAnswered = DateTime.UtcNow;
        var lastHeartbeatRef = -1;

        var reader = ReadAsync();
        await transport.SendAsync(PhoenixProtocol.Join(hrTopic, joinRef), ct).ConfigureAwait(false);
        var first = await Task.WhenAny(joined.Task, reader, Task.Delay(Timings.RequestTimeout, ct)).ConfigureAwait(false);
        if (first != joined.Task)
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(transport.CloseReason ?? "HypeRate did not answer the channel join");
        }
        var reply = joined.Task.Result;
        if (PhoenixProtocol.ReplyStatus(reply) != "ok")
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            throw new IntegrationAuthException($"HypeRate refused to join session '{id}' ({PhoenixProtocol.ReplyReason(reply)}). Check the ID.");
        }
        MarkConnected($"Connected to HypeRate session {id}; waiting for heart rate data",
            new Dictionary<string, object?> { ["sessionId"] = id });
        if (Settings.SubscribeClips) await transport.SendAsync(PhoenixProtocol.Join(clipsTopic, Next()), ct).ConfigureAwait(false);

        var heartbeat = HeartbeatAsync();
        try
        {
            var ended = await Task.WhenAny(reader, heartbeat).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            await ended.ConfigureAwait(false); // surfaces the failure that ended the session
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await Swallow(reader).ConfigureAwait(false);
            await Swallow(heartbeat).ConfigureAwait(false);
        }

        async Task ReadAsync()
        {
            while (await transport.ReceiveAsync(session.Token).ConfigureAwait(false) is { } text)
            {
                if (PhoenixProtocol.Parse(text) is not { } message) continue;
                switch (message.Event)
                {
                    case "phx_reply" when message.Ref == joinRef.ToString(CultureInfo.InvariantCulture) && message.Topic == hrTopic:
                        joined.TrySetResult(message);
                        break;
                    case "phx_reply" when message.Topic == "phoenix":
                        if (message.Ref == Volatile.Read(ref lastHeartbeatRef).ToString(CultureInfo.InvariantCulture)) heartbeatAnswered = DateTime.UtcNow;
                        break;
                    case "phx_reply" when message.Topic == clipsTopic && PhoenixProtocol.ReplyStatus(message) != "ok":
                        Log.Warn(Name, $"Could not join the clips channel ({PhoenixProtocol.ReplyReason(message)})");
                        break;
                    case "hr_update" when message.Topic == hrTopic:
                        if (message.Payload.Num("hr") is { } hr)
                        {
                            Report((int)Math.Round(hr));
                            SetStatus(ConnectionState.Connected, $"Connected to HypeRate session {id}: {(int)Math.Round(hr)} BPM");
                        }
                        break;
                    case "clip:created" when message.Topic == clipsTopic:
                    {
                        var slug = message.Payload.Str("twitch_slug");
                        var url = slug.Length > 0 ? "https://clips.twitch.tv/" + slug : "";
                        Publish("clipCreated", new Dictionary<string, object?>
                        {
                            ["twitchSlug"] = slug,
                            ["clipUrl"] = url,
                            ["url"] = url,   // Streamer.bot's name
                        });
                        break;
                    }
                    case "phx_error" or "phx_close" when message.Topic == hrTopic:
                        throw new IOException($"HypeRate closed the channel ({message.Event})");
                }
            }
        }

        async Task HeartbeatAsync()
        {
            while (true)
            {
                await Task.Delay(Timings.HeartbeatInterval, session.Token).ConfigureAwait(false);
                if (DateTime.UtcNow - heartbeatAnswered > Timings.HeartbeatInterval * 3)
                    throw new TimeoutException("HypeRate stopped answering heartbeats");
                var reference = Next();
                Volatile.Write(ref lastHeartbeatRef, reference);
                await transport.SendAsync(PhoenixProtocol.Heartbeat(reference), session.Token).ConfigureAwait(false);
            }
        }
    }

    private int Next() => Interlocked.Increment(ref _ref);

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported or the session is over.
        }
    }
}
