using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamDeck.Tests;

/// <summary>The plugin channel through the real REST API listener (random port, bearer token).</summary>
public class EndToEndTests
{
    private const string Token = "sd-test-token";

    private static async Task<(Harness Harness, ServerManager Manager)> StartAsync()
    {
        var h = new Harness();
        var services = new ServerServices
        {
            Engine = h.Engine, Profile = h.Profile, Globals = h.Globals, Bus = h.Bus, Log = h.Log, Registry = h.Registry,
            WebSocketRoutes = [h.Hub],
        };
        var manager = new ServerManager(null, services);
        manager.Settings.Api.Enabled = true;
        manager.Settings.Api.Port = 0;
        manager.Settings.Api.Token = Token;
        await manager.StartAsync();
        return (h, manager);
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string? token = Token)
    {
        var socket = new ClientWebSocket();
        if (token is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/v1/streamdeck"), timeout.Token);
        return socket;
    }

    private static async Task<JsonObject> ReceiveAsync(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var json = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!.AsObject();
            if ((string?)json["type"] == type) return json;
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task Plugin_connects_with_the_api_token_and_drives_cuebot()
    {
        var (h, manager) = await StartAsync();
        await using (h)
        await using (manager)
        {
            await h.AddActionAsync("Hello");
            using var socket = await ConnectAsync(manager.Api.Port);
            var welcome = await ReceiveAsync(socket, "welcome");
            Assert.Equal(1, (int?)welcome["protocol"]);

            await SendAsync(socket, Harness.Hello($"[{Harness.Button("ctx", "key-1")}]"));
            await SendAsync(socket, """{"type":"request","id":7,"method":"getActions"}""");
            var response = await ReceiveAsync(socket, "response");
            Assert.Equal(7, (int?)response["id"]);
            Assert.Equal("Hello", (string?)response["result"]!["actions"]![0]!["name"]);
            Assert.Equal(ConnectionState.Connected, h.Hub.State);

            Assert.Equal(1, await h.Hub.SendCommandAsync(new StreamDeckCommand("setTitle", "key-1", Title: "Hi")));
            var command = await ReceiveAsync(socket, "command");
            Assert.Equal("Hi", (string?)command["title"]);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            for (var i = 0; i < 200 && h.Hub.State == ConnectionState.Connected; i++) await Task.Delay(25);
            Assert.Equal(ConnectionState.Disconnected, h.Hub.State);
            Assert.Single(h.EventsOf(StreamDeckHub.DisconnectedEvent));
        }
    }

    [Fact]
    public async Task Wrong_or_missing_token_is_refused()
    {
        var (h, manager) = await StartAsync();
        await using (h)
        await using (manager)
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(manager.Api.Port, token: null));
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectAsync(manager.Api.Port, token: "wrong"));
            Assert.Empty(h.Hub.Instances);
        }
    }

    [Fact]
    public async Task Plain_http_to_the_socket_path_says_upgrade_required()
    {
        var (h, manager) = await StartAsync();
        await using (h)
        await using (manager)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
            var response = await http.GetAsync($"http://127.0.0.1:{manager.Api.Port}/api/v1/streamdeck");
            Assert.Equal(426, (int)response.StatusCode);
            var other = await http.GetAsync($"http://127.0.0.1:{manager.Api.Port}/api/v1/notasocket");
            Assert.Equal(404, (int)other.StatusCode);
        }
    }

    [Fact]
    public void Registration_exposes_one_hub_and_every_sub_action()
    {
        var services = new ServiceCollection();
        var log = new AppLog();
        var bus = new EventBus(log);
        services.AddSingleton(log);
        services.AddSingleton(bus);
        services.AddSingleton(new ProfileService(null, log));
        services.AddSingleton(new GlobalVariables(null, log));
        services.AddSingleton(sp => new ServerServices(sp));
        var data = Directory.CreateTempSubdirectory("cuebot-sd-registration-");
        services.AddSingleton(new AppPaths(data.FullName));
        services.AddStreamDeck();
        using var provider = services.BuildServiceProvider();

        var hub = provider.GetRequiredService<StreamDeckHub>();
        var usb = provider.GetRequiredService<Cuebot.StreamDeck.Hid.StreamDeckHidService>();
        Assert.Equal(new IConnectionStatus[] { hub, usb }, provider.GetServices<IConnectionStatus>());   // direct USB mode has its own dashboard line
        Assert.Equal("Not started", usb.StatusMessage);   // nothing touches USB until StartAsync
        Assert.Same(hub, provider.GetServices<Cuebot.Server.Api.IApiWebSocketRoute>().Single());
        Assert.Same(hub, provider.GetRequiredService<ServerServices>().WebSocketRoutes.Single());

        var handlers = provider.GetServices<ISubActionHandler>().ToArray();
        Assert.Equal(StreamDeckSubAction.TypeIds, handlers.Select(x => x.TypeId));
        Assert.All(handlers, x => Assert.Equal("Stream Deck", x.Category));
        Assert.Equal(["Set key picture", "Set State", "Set Title", "Set key value", "Show Alert", "Show Ok", "Toggle State"],
            handlers.Select(x => x.DisplayName));

        var triggers = provider.GetServices<ITriggerProvider>().SelectMany(p => p.GetTriggers()).Select(t => t.DisplayName);
        Assert.Equal(["Stream Deck key or dial used", "Stream Deck Connected", "Stream Deck Disconnected", "Stream Deck devices changed"], triggers);

        var functions = provider.GetServices<IScriptFunctionProvider>().SelectMany(p => p.GetFunctions()).Select(f => f.Name).ToArray();
        Assert.Contains("streamdeck.setTitle", functions);
        Assert.Contains("streamdeck.getInstances", functions);
        Assert.Contains("REST API is off", hub.StatusMessage);
    }

    [Fact]
    public async Task Sub_action_sets_the_title_and_reports_how_many_keys_it_reached()
    {
        await using var h = new Harness();
        var peer = await h.ConnectAsync(buttons: $"[{Harness.Button("c1", "score")}]");
        var handler = StreamDeckSubAction.Create(h.Hub).Single(x => x.TypeId == "streamDeck.setTitle");
        var action = new ActionDefinition
        {
            Name = "Score",
            SubActions = [new SubActionDefinition { Type = "streamDeck.setTitle", Parameters = new() { ["buttonId"] = "score", ["title"] = "%points%" } }],
        };
        Assert.NotNull(handler);
        var registry = new Registry([handler], []);
        using var engine = new ActionEngine(h.Bus, h.Profile, registry, h.Globals, h.Log);
        engine.Start();
        var ok = await engine.RunAsync(action, new Dictionary<string, object?> { ["points"] = 12 }, CancellationToken.None, "test");
        Assert.True(ok);
        var command = Assert.Single(peer.OfType("command"));
        Assert.Equal("12", (string?)command["title"]);
    }
}
