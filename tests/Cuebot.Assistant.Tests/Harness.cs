using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server;

namespace Cuebot.Assistant.Tests;

/// <summary>A Cuebot with the MCP tools (no listener) and one action, "Hello".</summary>
internal sealed class Bot : IAsyncDisposable
{
    public Bot(bool requireConfirmation = true)
    {
        Bus = new EventBus(Log);
        Profile = new ProfileService(null, Log);
        Profile.EditAsync(p => p.Actions.Add(new ActionDefinition { Name = "Hello" })).GetAwaiter().GetResult();
        var registry = new Registry([], [], []);
        var engine = new ActionEngine(Bus, Profile, registry, new GlobalVariables(null, Log), Log);
        Manager = new ServerManager(null, new ServerServices { Engine = engine, Profile = Profile, Globals = new GlobalVariables(null, Log), Bus = Bus, Log = Log, Registry = registry });
        Manager.Settings.Mcp.RequireConfirmation = requireConfirmation;
        Tools = new AssistantTools(Manager.Mcp);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public ProfileService Profile { get; }
    public ServerManager Manager { get; }
    public AssistantTools Tools { get; }

    public AssistantContext Context(string model = "") => new("You help.", Tools, null, model, Path.GetTempPath());

    public ValueTask DisposeAsync() => Manager.DisposeAsync();
}

/// <summary>Answers HTTP requests from a script and records them.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public JsonObject[] Bodies => [.. Requests.Where(r => r.Body.Length > 0).Select(r => (JsonObject)JsonNode.Parse(r.Body)!)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A server-sent event stream: each item is one "data:" line (or a full "event:...\ndata:..." block).</summary>
    public static HttpResponseMessage Sse(IEnumerable<string> events) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(e => (e.StartsWith("event:", StringComparison.Ordinal) ? e : "data: " + e) + "\n\n")),
                Encoding.UTF8, "text/event-stream"),
        };
}

internal static class Updates
{
    public static async Task<List<AssistantUpdate>> CollectAsync(IAsyncEnumerable<AssistantUpdate> updates, bool approve = true)
    {
        var list = new List<AssistantUpdate>();
        await foreach (var update in updates)
        {
            list.Add(update);
            if (update is AssistantConfirmation c)
            {
                if (approve) c.Approve();
                else c.Deny();
            }
        }
        return list;
    }

    public static string Text(this IEnumerable<AssistantUpdate> updates) => string.Concat(updates.OfType<AssistantText>().Select(t => t.Text));
}
