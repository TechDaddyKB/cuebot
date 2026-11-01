using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Discord.Gateway;

namespace Cuebot.Discord.Tests.Support;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

internal static class TestToken
{
    /// <summary>A token-shaped test value, built from pieces so no source file holds token-shaped text.</summary>
    public static readonly string Value = "MTAwMDAwMDAwMDAwMDAwMDAw" + "MA" + ".Test01." + new string('t', 30);
}

/// <summary>An in-memory Discord Gateway: every connection the bot opens shows up in <see cref="NextAsync"/>.</summary>
internal sealed class FakeGateway
{
    private readonly Channel<FakeGatewaySocket> _accepted = Channel.CreateUnbounded<FakeGatewaySocket>();
    private readonly ConcurrentQueue<Uri> _urls = new();

    public IReadOnlyList<Uri> Urls => _urls.ToArray();

    public IGatewaySocket Create() => new FakeGatewaySocket(this);

    internal void Accept(FakeGatewaySocket socket, Uri url)
    {
        _urls.Enqueue(url);
        _accepted.Writer.TryWrite(socket);
    }

    public async Task<FakeGatewaySocket> NextAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _accepted.Reader.ReadAsync(cts.Token);
    }
}

internal sealed class FakeGatewaySocket(FakeGateway gateway) : IGatewaySocket
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<JsonObject> _fromClient = Channel.CreateUnbounded<JsonObject>();
    private int _seq;

    public Uri? Url { get; private set; }
    public int? CloseCode { get; private set; }
    public string? CloseReason { get; private set; }

    /// <summary>The code the bot closed with (4000 keeps the session resumable).</summary>
    public int? ClientCloseCode { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        Url = url;
        gateway.Accept(this, url);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        _fromClient.Writer.TryWrite(JsonNode.Parse(text)!.AsObject());
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _toClient.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task CloseAsync(int code, CancellationToken ct)
    {
        ClientCloseCode ??= code;
        _toClient.Writer.TryWrite(null);
        return Task.CompletedTask;
    }

    public void Push(object payload) => _toClient.Writer.TryWrite(JsonSerializer.Serialize(payload));

    public void Hello(int intervalMs = 45000) => Push(new { op = 10, d = new { heartbeat_interval = intervalMs } });

    public void Dispatch(string name, object data) => Push(new { op = 0, s = ++_seq, t = name, d = data });

    public void Ready(string sessionId = "session-1", string resumeUrl = "wss://resume.example") =>
        Dispatch("READY", new
        {
            v = 10, session_id = sessionId, resume_gateway_url = resumeUrl,
            user = new { id = "100000000000000001", username = "CuebotTestBot", bot = true },
            application = new { id = "100000000000000001" },
            guilds = new[] { new { id = Ids.Guild, unavailable = true } },
        });

    public void GuildCreate() => Dispatch("GUILD_CREATE", new
    {
        id = Ids.Guild, name = "My Server",
        channels = new object[]
        {
            new { id = Ids.General, name = "general", type = 0, position = 0 },
            new { id = Ids.Announcements, name = "announcements", type = 5, position = 1 },
            new { id = "200000000000000099", name = "Voice", type = 2, position = 2 },
        },
        roles = new object[]
        {
            new { id = Ids.Guild, name = "@everyone", position = 0, managed = false },
            new { id = Ids.LiveRole, name = "Live notifications", position = 1, managed = false },
            new { id = "300000000000000099", name = "CuebotTestBot", position = 2, managed = true },
        },
    });

    /// <summary>Closes the connection from Discord's side with a close code.</summary>
    public void CloseFromServer(int? code, string reason = "closed by Discord")
    {
        CloseCode = code;
        CloseReason = code is { } c ? $"closed by Discord ({c})" : reason;
        _toClient.Writer.TryWrite(null);
    }

    public async Task<JsonObject> NextSentAsync(int? op = null)
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        while (true)
        {
            var message = await _fromClient.Reader.ReadAsync(cts.Token);
            if (op is null || message["op"]!.GetValue<int>() == op) return message;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Ids
{
    public const string Guild = "400000000000000001";
    public const string General = "200000000000000001";
    public const string Announcements = "200000000000000002";
    public const string LiveRole = "300000000000000001";
    public const string User = "500000000000000001";
}

/// <summary>A recorded REST call.</summary>
internal sealed record FakeRequest(HttpMethod Method, string Path, string Body, string? Authorization, IReadOnlyDictionary<string, string> Headers)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
}

/// <summary>An in-memory Discord REST API: answers by a handler, records every call.</summary>
internal sealed class FakeDiscordHttp : HttpMessageHandler
{
    private readonly ConcurrentQueue<FakeRequest> _requests = new();

    public Func<FakeRequest, HttpResponseMessage?> Handler { get; set; } = _ => null;

    public IReadOnlyList<FakeRequest> Requests => _requests.ToArray();

    public FakeRequest[] Matching(HttpMethod method, string pathStart) =>
        Requests.Where(r => r.Method == method && r.Path.StartsWith(pathStart, StringComparison.Ordinal)).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.PathAndQuery;
        const string prefix = "/api/v10/";
        if (path.StartsWith(prefix, StringComparison.Ordinal)) path = path[prefix.Length..];
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        headers["User-Agent"] = request.Headers.UserAgent.ToString();
        var recorded = new FakeRequest(request.Method, path, body, request.Headers.Authorization?.ToString(), headers);
        _requests.Enqueue(recorded);
        return Handler(recorded) ?? Default(recorded);
    }

    private static HttpResponseMessage Default(FakeRequest r) => r switch
    {
        { Path: "gateway/bot" } => Json(new { url = "wss://gateway.example", shards = 1 }),
        { Path: "applications/@me" } => Json(new { id = "100000000000000001", name = "CuebotTestBot" }),
        { Method.Method: "POST", Path: var p } when p.StartsWith("channels/", StringComparison.Ordinal) =>
            Json(new { id = "600000000000000001", channel_id = p.Split('/')[1] }),
        { Method.Method: "POST", Path: "users/@me/channels" } => Json(new { id = "700000000000000001", type = 1 }),
        { Method.Method: "PUT", Path: var p } when p.Contains("/commands", StringComparison.Ordinal) => Json(Array.Empty<object>()),
        _ => new HttpResponseMessage(HttpStatusCode.NoContent),
    };

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK, IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) response.Headers.TryAddWithoutValidation(k, v);
        return response;
    }
}

/// <summary>A temp data folder with discord-bot.json, a log, a recording event bus, a fake Gateway and REST API.</summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    public static readonly DiscordTimings Fast = DiscordTimings.Default with
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReconnectDelay = TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
        InvalidSessionMinDelay = TimeSpan.FromMilliseconds(10),
        InvalidSessionMaxDelay = TimeSpan.FromMilliseconds(20),
        ReplyGrace = TimeSpan.FromMilliseconds(300),
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-discord-tests-").FullName;

    public TestEnvironment(bool withToken = true)
    {
        Settings = new JsonFile<DiscordBotSettings>(Path.Combine(_dir, "discord-bot.json"), Log);
        if (withToken) Settings.Value.BotToken = TestToken.Value;
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
        Bot = new DiscordBotService(Settings, Bus, Log, new HttpClient(Http), Gateway.Create, Fast);
    }

    public string Dir => _dir;
    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<DiscordBotSettings> Settings { get; }
    public FakeGateway Gateway { get; } = new();
    public FakeDiscordHttp Http { get; } = new();
    public DiscordBotService Bot { get; }
    public ConcurrentQueue<BotEvent> Published { get; } = new();

    public BotEvent[] Events(string type) => Published.Where(e => e.Type == type).ToArray();

    public async Task<BotEvent> WaitForEventAsync(string type)
    {
        BotEvent? found = null;
        await Wait.UntilAsync(() => (found = Published.FirstOrDefault(e => e.Type == type)) is not null, type);
        return found!;
    }

    /// <summary>Connects and completes hello → identify → READY (+ one server).</summary>
    public async Task<FakeGatewaySocket> ConnectReadyAsync()
    {
        await Bot.ConnectAsync();
        var socket = await Gateway.NextAsync();
        socket.Hello();
        await socket.NextSentAsync(GatewayOp.Identify);
        socket.Ready();
        socket.GuildCreate();
        await Wait.UntilAsync(() => Bot.State == ConnectionState.Connected && Bot.Guilds.Count == 1, "ready");
        return socket;
    }

    public async ValueTask DisposeAsync()
    {
        await Bot.DisposeAsync();
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
