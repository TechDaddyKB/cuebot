using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.Hosting;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.SpeakerBot;

/// <summary>
/// Speaker.bot-compatible WebSocket server (default port 7580): <c>{request, id, ...}</c> → <c>{status, id, ...}</c>.
/// Clients that send Subscribe (optionally <c>events: ["SpeechStarted", ...]</c>) receive speech events as
/// <c>{timeStamp, event: {source: "SpeakerBot", type}, data}</c>.
/// </summary>
public sealed class SpeakerBotWebSocketServer(SpeakerBotSettings settings, SpeakerBotApi api, Func<IReadOnlyCollection<string>>? origins = null)
    : IWebSocketHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new();
    private WebSocketEndpoint? _endpoint;

    public int Port => _endpoint?.Port ?? 0;
    public bool IsRunning => _endpoint is not null;
    public int ClientCount => _endpoint?.Connections.Count ?? 0;
    public event Action? ClientsChanged;

    public async Task StartAsync(IPAddress address, CancellationToken ct = default)
    {
        if (_endpoint is not null) return;
        _endpoint = await WebSocketEndpoint.StartAsync(address, settings.WebSocketPort, settings.Endpoint, this,
            origins ?? (() => []), ct).ConfigureAwait(false);
    }

    public async Task<int> PublishAsync(BotEvent e)
    {
        if (_endpoint is null || _subscriptions.IsEmpty || SpeakerBotApi.MapEvent(e) is not { } mapped) return 0;
        var text = StreamerBotEnvelope.Create(new StreamerBotEventName("SpeakerBot", mapped.Type), mapped.Data, e.Time).ToJsonString();
        return await _endpoint.BroadcastAsync(text, c => _subscriptions.TryGetValue(c.Id, out var types)
            && (types.Count == 0 || types.Contains(mapped.Type) || types.Contains("*"))).ConfigureAwait(false);
    }

    Task IWebSocketHandler.OnOpenAsync(WebSocketConnection connection)
    {
        ClientsChanged?.Invoke();
        return connection.SendAsync(new JsonObject
        {
            ["timeStamp"] = DateTimeOffset.Now.ToString("o"),
            ["session"] = connection.Id,
            ["request"] = "Hello",
            ["info"] = new JsonObject { ["name"] = "Cuebot", ["source"] = "cuebot" },
        }.ToJsonString());
    }

    async Task IWebSocketHandler.OnMessageAsync(WebSocketConnection connection, string text)
    {
        JsonNode? id = null;
        JsonObject response;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var message = doc.RootElement.Clone();
            if (message.ValueKind != JsonValueKind.Object) throw new RequestException("Invalid request");
            if (message.TryGetProperty("id", out var idElement)) id = JsonNode.Parse(idElement.GetRawText());
            var request = StreamerBotRequests.Str(message, "request");
            response = request.ToLowerInvariant() switch
            {
                "subscribe" => Subscribe(connection, message, true),
                "unsubscribe" => Subscribe(connection, message, false),
                "" => throw new RequestException("Missing request"),
                _ => await api.HandleRequestAsync(request, message).ConfigureAwait(false),
            };
            response["status"] = "ok";
        }
        catch (JsonException)
        {
            response = new JsonObject { ["status"] = "error", ["error"] = "Invalid JSON" };
        }
        catch (RequestException ex)
        {
            response = new JsonObject { ["status"] = "error", ["error"] = ex.Message };
        }
        catch (Exception ex)
        {
            response = new JsonObject { ["status"] = "error", ["error"] = $"Request failed: {ex.Message}" };
        }
        response["id"] = id;
        await connection.SendAsync(response.ToJsonString()).ConfigureAwait(false);
    }

    void IWebSocketHandler.OnClose(WebSocketConnection connection)
    {
        _subscriptions.TryRemove(connection.Id, out _);
        ClientsChanged?.Invoke();
    }

    private JsonObject Subscribe(WebSocketConnection connection, JsonElement message, bool subscribe)
    {
        var types = message.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array
            ? events.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];
        if (subscribe)
        {
            var set = _subscriptions.GetOrAdd(connection.Id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (set) foreach (var t in types) set.Add(t);
        }
        else if (types.Length == 0)
        {
            _subscriptions.TryRemove(connection.Id, out _);
        }
        else if (_subscriptions.TryGetValue(connection.Id, out var set))
        {
            lock (set) foreach (var t in types) set.Remove(t);
        }
        var current = _subscriptions.TryGetValue(connection.Id, out var now) ? now.Order(StringComparer.Ordinal).ToArray() : [];
        return new JsonObject
        {
            ["subscribed"] = _subscriptions.ContainsKey(connection.Id),
            ["events"] = new JsonArray(current.Select(t => (JsonNode)t).ToArray()),
        };
    }

    public async ValueTask DisposeAsync()
    {
        var endpoint = Interlocked.Exchange(ref _endpoint, null);
        if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false);
        _subscriptions.Clear();
    }
}
