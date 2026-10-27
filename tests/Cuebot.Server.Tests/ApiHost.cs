using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Server.Api;
using Cuebot.Server.Mcp;

namespace Cuebot.Server.Tests;

/// <summary>A TestBot with the REST API and MCP listening on a random port, and clients for both.</summary>
internal sealed class ApiHost : IAsyncDisposable
{
    public const string Token = "test-token-123";

    private ApiHost(TestBot bot, HttpClient http)
    {
        Bot = bot;
        Http = http;
    }

    public TestBot Bot { get; }

    /// <summary>Authorized client with BaseAddress http://127.0.0.1:port/.</summary>
    public HttpClient Http { get; }

    public int Port => Bot.Manager.Api.Port;
    public Uri McpUri => new($"http://127.0.0.1:{Port}/mcp");
    public string? Session { get; set; }

    public static async Task<ApiHost> StartAsync(bool api = true, bool mcp = true, bool confirm = true, string token = Token)
    {
        var bot = new TestBot(new ServerSettings
        {
            Api = new ApiServerSettings { Enabled = api, Port = 0, Token = token },
            Mcp = new McpServerSettings { Enabled = mcp, RequireConfirmation = confirm },
        });
        await bot.Manager.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bot.Manager.Api.Port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ApiHost(bot, http);
    }

    public async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var text = body is JsonNode node ? node.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body);
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }
        using var response = await Http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, content.Length == 0 ? null : JsonNode.Parse(content));
    }

    public Task<(int Status, JsonNode? Body)> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<(int Status, JsonNode? Body)> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    // ---- MCP ----

    private int _nextId;

    /// <summary>Posts one JSON-RPC message to /mcp; returns the HTTP status and the parsed body (null for 202).</summary>
    public async Task<(int Status, JsonNode? Body, HttpResponseMessage Response)> McpPostAsync(JsonNode message, bool withSession = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (withSession && Session is not null) request.Headers.Add(McpServer.SessionHeader, Session);
        var response = await Http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, content.Length == 0 ? null : JsonNode.Parse(content), response);
    }

    /// <summary>Sends a request and returns its "result" (fails on a JSON-RPC error).</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject? parameters = null)
    {
        var response = await RpcAsync(method, parameters);
        Assert.True(response["error"] is null, $"{method} failed: {response.ToJsonString()}");
        return response["result"]!.AsObject();
    }

    /// <summary>Sends a request and returns the whole JSON-RPC response.</summary>
    public async Task<JsonObject> RpcAsync(string method, JsonObject? parameters = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        var (status, body, _) = await McpPostAsync(message);
        Assert.Equal(200, status);
        Assert.Equal(id, body!["id"]!.GetValue<int>());
        return body.AsObject();
    }

    public async Task<JsonObject> InitializeAsync(string version = McpServer.LatestProtocolVersion)
    {
        var (status, body, response) = await McpPostAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 0, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1.0" },
            },
        }, withSession: false);
        Assert.Equal(200, status);
        Session = response.Headers.GetValues(McpServer.SessionHeader).Single();
        var (notified, _, _) = await McpPostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        Assert.Equal(202, notified);
        return body!["result"]!.AsObject();
    }

    /// <summary>tools/call; returns (isError, structuredContent).</summary>
    public async Task<(bool IsError, JsonObject Content)> CallAsync(string tool, JsonObject? args = null)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] });
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var structured = result["structuredContent"]?.AsObject() ?? JsonNode.Parse(text)!.AsObject();
        Assert.Equal(JsonNode.Parse(text)!.ToJsonString(), structured.ToJsonString());
        return (result["isError"]!.GetValue<bool>(), structured);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Bot.DisposeAsync();
    }
}
