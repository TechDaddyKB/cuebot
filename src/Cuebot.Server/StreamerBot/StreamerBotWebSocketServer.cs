using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.Hosting;

namespace Cuebot.Server.StreamerBot;

/// <summary>
/// Streamer.bot-compatible WebSocket server: Hello (with salt/challenge when a password is set), Authenticate,
/// Subscribe/UnSubscribe and the requests of <see cref="StreamerBotRequests"/>; pushes subscribed events in
/// Streamer.bot's envelope. Requests are <c>{request, id, ...}</c>; responses <c>{status, id, ...}</c>.
/// </summary>
public sealed class StreamerBotWebSocketServer : IWebSocketHandler, IAsyncDisposable
{
    private readonly StreamerBotWebSocketSettings _settings;
    private readonly StreamerBotRequests _requests;
    private readonly Func<IReadOnlyCollection<string>> _origins;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private WebSocketEndpoint? _endpoint;

    public StreamerBotWebSocketServer(StreamerBotWebSocketSettings settings, StreamerBotRequests requests,
        Func<IReadOnlyCollection<string>>? origins = null)
    {
        _settings = settings;
        _requests = requests;
        _origins = origins ?? (() => []);
    }

    public int Port => _endpoint?.Port ?? 0;
    public bool IsRunning => _endpoint is not null;
    public event Action? ClientsChanged;

    public IReadOnlyList<ConnectedClient> Clients =>
        _sessions.Values.OrderBy(s => s.Connection.ConnectedAt)
            .Select(s => new ConnectedClient(s.Connection.Id, s.Connection.Remote, s.Connection.ConnectedAt, s.Authenticated,
                s.SubscriptionCount))
            .ToArray();

    private bool AuthRequired => _settings.AuthenticationEnabled && _settings.Password.Length > 0;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_endpoint is not null) return;
        _endpoint = await WebSocketEndpoint.StartAsync(address, _settings.Port, _settings.Endpoint, this, _origins, ct).ConfigureAwait(false);
    }

    /// <summary>Forwards a bus event to the sessions subscribed to its Streamer.bot name.</summary>
    public Task<int> PublishAsync(BotEvent e)
    {
        if (_sessions.IsEmpty) return Task.FromResult(0);
        var (name, message) = StreamerBotEnvelope.FromBotEvent(e);
        return SendEventAsync(name, message);
    }

    /// <summary>Whether any connected session is subscribed to <paramref name="name"/> (lets callers skip building costly payloads).</summary>
    public bool HasSubscribers(StreamerBotEventName name) => _sessions.Values.Any(s => s.IsSubscribed(name));

    /// <summary>Sends an event (already in envelope form) to subscribed, authorized sessions.</summary>
    public async Task<int> SendEventAsync(StreamerBotEventName name, JsonObject message)
    {
        var targets = _sessions.Values.Where(s => s.IsSubscribed(name) && (!AuthRequired || s.Authenticated || !_settings.EnforceAuthentication)).ToArray();
        if (targets.Length == 0) return 0;
        var text = message.ToJsonString();
        var sent = await Task.WhenAll(targets.Select(s => s.Connection.SendAsync(text))).ConfigureAwait(false);
        return sent.Count(x => x);
    }

    /// <summary>CPH.WebsocketBroadcastJson / String: General.Custom to every (authorized) client, subscribed or not.</summary>
    public async Task<int> BroadcastCustomAsync(string data, bool json)
    {
        JsonNode? payload = data;
        if (json)
        {
            try { payload = JsonNode.Parse(data); }
            catch (JsonException) { payload = data; }
        }
        var message = StreamerBotEnvelope.Create(new StreamerBotEventName("General", "Custom"), payload, DateTimeOffset.Now).ToJsonString();
        var targets = _sessions.Values.Where(s => !AuthRequired || s.Authenticated || !_settings.EnforceAuthentication).ToArray();
        var sent = await Task.WhenAll(targets.Select(s => s.Connection.SendAsync(message))).ConfigureAwait(false);
        return sent.Count(x => x);
    }

    async Task IWebSocketHandler.OnOpenAsync(WebSocketConnection connection)
    {
        var session = new Session(connection);
        _sessions[connection.Id] = session;
        var hello = new JsonObject
        {
            ["timeStamp"] = DateTimeOffset.Now.ToString("o"),
            ["session"] = connection.Id,
            ["request"] = "Hello",
            ["info"] = StreamerBotRequests.Info(),
        };
        if (AuthRequired)
        {
            hello["authentication"] = new JsonObject { ["salt"] = session.Salt, ["challenge"] = session.Challenge };
        }
        await connection.SendAsync(hello.ToJsonString()).ConfigureAwait(false);
        ClientsChanged?.Invoke();
    }

    async Task IWebSocketHandler.OnMessageAsync(WebSocketConnection connection, string message)
    {
        if (!_sessions.TryGetValue(connection.Id, out var session)) return;
        var response = await HandleAsync(session, message).ConfigureAwait(false);
        if (response is not null) await connection.SendAsync(response.ToJsonString()).ConfigureAwait(false);
    }

    void IWebSocketHandler.OnClose(WebSocketConnection connection)
    {
        _sessions.TryRemove(connection.Id, out _);
        ClientsChanged?.Invoke();
    }

    private async Task<JsonObject?> HandleAsync(Session session, string text)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(text);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(null, "Invalid JSON");
        }
        if (message.ValueKind != JsonValueKind.Object) return Error(null, "Invalid request");

        var id = message.TryGetProperty("id", out var idElement) ? JsonNode.Parse(idElement.GetRawText()) : null;
        var request = StreamerBotRequests.Str(message, "request");
        if (request.Length == 0) return Error(id, "Missing request");

        try
        {
            JsonObject body;
            if (string.Equals(request, "Authenticate", StringComparison.OrdinalIgnoreCase))
            {
                body = Authenticate(session, message);
            }
            else
            {
                if (AuthRequired && !session.Authenticated
                    && (_settings.EnforceAuthentication || StreamerBotRequests.Privileged.Contains(request)))
                {
                    return Error(id, "Authentication required");
                }
                body = request.ToLowerInvariant() switch
                {
                    "subscribe" => new JsonObject { ["events"] = session.Change(message, subscribe: true) },
                    "unsubscribe" => new JsonObject { ["events"] = session.Change(message, subscribe: false) },
                    _ => await _requests.HandleAsync(request, message).ConfigureAwait(false),
                };
                if (request.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                    || request.Equals("unsubscribe", StringComparison.OrdinalIgnoreCase))
                {
                    ClientsChanged?.Invoke();
                }
            }
            body["status"] = "ok";
            body["id"] = id;
            return body;
        }
        catch (RequestException ex)
        {
            return Error(id, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(id, $"Request failed: {ex.Message}");
        }
    }

    private JsonObject Authenticate(Session session, JsonElement message)
    {
        if (!AuthRequired) return [];
        if (!StreamerBotAuth.Verify(_settings.Password, session.Salt, session.Challenge, StreamerBotRequests.Str(message, "authentication")))
        {
            throw new RequestException("Authentication failed");
        }
        session.Authenticated = true;
        ClientsChanged?.Invoke();
        return [];
    }

    private static JsonObject Error(JsonNode? id, string error) => new()
    {
        ["status"] = "error",
        ["error"] = error,
        ["id"] = id,
    };

    public async ValueTask DisposeAsync()
    {
        var endpoint = Interlocked.Exchange(ref _endpoint, null);
        if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }

    /// <summary>One client: authentication state and subscriptions (source → types, "*" for all types).</summary>
    private sealed class Session(WebSocketConnection connection)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, HashSet<string>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

        public WebSocketConnection Connection { get; } = connection;
        public string Salt { get; } = StreamerBotAuth.NewToken();
        public string Challenge { get; } = StreamerBotAuth.NewToken();
        public bool Authenticated { get; set; }

        public int SubscriptionCount
        {
            get { lock (_gate) return _subscriptions.Values.Sum(s => s.Count); }
        }

        public bool IsSubscribed(StreamerBotEventName name)
        {
            lock (_gate)
            {
                return (_subscriptions.TryGetValue(name.Source, out var types) && (types.Contains(name.Type) || types.Contains("*")))
                       || (_subscriptions.TryGetValue("*", out var any) && (any.Contains(name.Type) || any.Contains("*")));
            }
        }

        /// <summary>Applies <c>events: {Source: [Type...]}</c> and returns the full subscription list.</summary>
        public JsonObject Change(JsonElement message, bool subscribe)
        {
            if (!message.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object)
            {
                throw new RequestException("events is required");
            }
            lock (_gate)
            {
                foreach (var source in events.EnumerateObject())
                {
                    var types = source.Value.ValueKind == JsonValueKind.Array
                        ? source.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray()
                        : source.Value.ValueKind == JsonValueKind.String ? [source.Value.GetString()!] : [];
                    if (subscribe)
                    {
                        if (!_subscriptions.TryGetValue(source.Name, out var set))
                        {
                            _subscriptions[source.Name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                        foreach (var t in types) set.Add(t);
                    }
                    else if (_subscriptions.TryGetValue(source.Name, out var set))
                    {
                        foreach (var t in types) set.Remove(t);
                        if (types.Contains("*") || set.Count == 0) _subscriptions.Remove(source.Name);
                    }
                }
                var result = new JsonObject();
                foreach (var (source, set) in _subscriptions.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    result[source] = new JsonArray(set.Order(StringComparer.Ordinal).Select(t => (JsonNode)t).ToArray());
                }
                return result;
            }
        }
    }
}

/// <summary>A connected API client, for the UI.</summary>
public sealed record ConnectedClient(string Id, string Remote, DateTimeOffset ConnectedAt, bool Authenticated, int Subscriptions);
