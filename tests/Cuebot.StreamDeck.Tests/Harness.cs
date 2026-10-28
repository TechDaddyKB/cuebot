using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server;
using Cuebot.Server.Api;

namespace Cuebot.StreamDeck.Tests;

/// <summary>A plugin connection that records what Cuebot sends.</summary>
internal sealed class FakePeer(string id = "peer-1") : IStreamDeckPeer
{
    public ConcurrentQueue<JsonObject> Sent { get; } = new();
    public string Id => id;
    public string Remote => "127.0.0.1:5000";

    public Task<bool> SendAsync(string text, CancellationToken ct = default)
    {
        Sent.Enqueue(JsonNode.Parse(text)!.AsObject());
        return Task.FromResult(true);
    }

    public JsonObject[] OfType(string type) => [.. Sent.Where(m => (string?)m["type"] == type)];
}

/// <summary>Real core services (engine, profile, globals) and a hub, without sockets.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public Harness()
    {
        Bus = new EventBus(Log);
        Profile = new ProfileService(null, Log);
        Globals = new GlobalVariables(null, Log);
        Registry = new Registry([], [], [new StreamDeckTriggers()]);
        Engine = new ActionEngine(Bus, Profile, Registry, Globals, Log);
        Engine.Start();
        Services = new ServerServices { Engine = Engine, Profile = Profile, Globals = Globals, Bus = Bus, Log = Log, Registry = Registry };
        Operations = new ApiOperations(Services);
        Hub = new StreamDeckHub(Bus, Log, () => Operations, () => ApiEnabled, Profile, Globals);
        Bus.Subscribe(e => Events.Enqueue(e));
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public ProfileService Profile { get; }
    public GlobalVariables Globals { get; }
    public Registry Registry { get; }
    public ActionEngine Engine { get; }
    public ServerServices Services { get; }
    public ApiOperations Operations { get; }
    public StreamDeckHub Hub { get; }
    public bool ApiEnabled { get; set; } = true;
    public ConcurrentQueue<BotEvent> Events { get; } = new();

    public BotEvent[] EventsOf(string type) => [.. Events.Where(e => e.Type == type)];

    public static string Hello(string buttons = "[]") => $$$"""
        {"type":"hello","protocol":1,"plugin":{"uuid":"io.github.cuebot.streamdeck","version":"0.1.0"},
         "app":{"version":"7.1.0","platform":"windows","language":"en"},
         "devices":[{"id":"DEV1","name":"My XL","type":2,"size":{"columns":8,"rows":4}},
                    {"id":"DEV2","name":"My Plus","type":7,"size":{"columns":4,"rows":2}}],
         "buttons":{{{buttons}}}}
        """;

    public static string Button(string context, string buttonId, string kind = "trigger", string controller = "Keypad",
        string device = "DEV1", int row = 0, int column = 0, int state = 0) =>
        $$"""{"context":"{{context}}","buttonId":"{{buttonId}}","kind":"{{kind}}","controller":"{{controller}}","device":"{{device}}","row":{{row}},"column":{{column}},"state":{{state}}}""";

    public async Task<FakePeer> ConnectAsync(string id = "peer-1", string? buttons = null)
    {
        var peer = new FakePeer(id);
        await Hub.OpenAsync(peer);
        await Hub.HandleAsync(id, Hello(buttons ?? "[]"));
        return peer;
    }

    public async Task<ActionDefinition> AddActionAsync(string name, bool enabled = true)
    {
        var action = new ActionDefinition { Name = name, Enabled = enabled };
        await Profile.EditAsync(p => p.Actions.Add(action));
        return action;
    }

    public ValueTask DisposeAsync()
    {
        Engine.Dispose();
        Profile.Dispose();
        Globals.Dispose();
        Log.Dispose();
        return ValueTask.CompletedTask;
    }
}
