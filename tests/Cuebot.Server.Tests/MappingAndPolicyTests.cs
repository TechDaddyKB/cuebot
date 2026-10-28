using System.Net;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.Hosting;
using Cuebot.Server.Http;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Tests;

public sealed class MappingAndPolicyTests
{
    [Theory]
    [InlineData("twitch.chatMessage", "Twitch", "ChatMessage")]
    [InlineData("twitch.follow", "Twitch", "Follow")]
    [InlineData("twitch.resub", "Twitch", "ReSub")]
    [InlineData("twitch.giftBomb", "Twitch", "GiftBomb")]
    [InlineData("youtube.chatMessage", "YouTube", "Message")]
    [InlineData("youtube.superChat", "YouTube", "SuperChat")]
    [InlineData("kick.resubscription", "Kick", "Resubscription")]
    [InlineData("obs.streamStarted", "Obs", "StreamingStarted")]
    [InlineData("speechToText.command", "SpeechToText", "Command")]
    [InlineData("speechToText.dictation", "SpeechToText", "Dictation")]
    [InlineData("obs.recordStopped", "Obs", "RecordingStopped")]
    [InlineData("obs.sceneChanged", "Obs", "SceneChanged")]
    [InlineData("command.triggered", "Command", "Triggered")]
    [InlineData("groups.usersAdded", "Group", "UsersAdded")]
    [InlineData("variables.globalUpdated", "Misc", "GlobalVariableUpdated")]
    [InlineData("core.appStarted", "Misc", "StreamerbotStarted")]
    [InlineData("core.timer", "Misc", "TimedAction")]
    [InlineData("core.fileRenamed", "FileWatcher", "Renamed")]
    [InlineData("custom.codeEvent", "Custom", "CodeEvent")]
    [InlineData("websocket.clientMessage", "WebsocketClient", "Message")]
    [InlineData("websocket.serverOpen", "WebsocketCustomServer", "Open")]
    [InlineData("chat.message", "Chat", "Message")]
    [InlineData("rumble.rant", "Rumble", "Rant")]
    [InlineData("somemodule.thingHappened", "Somemodule", "ThingHappened")]
    public void Cuebot_events_map_to_streamerbot_names(string type, string source, string sbType)
    {
        var name = StreamerBotEventMap.Map(type);
        Assert.Equal(source, name.Source);
        Assert.Equal(sbType, name.Type);
    }

    [Fact]
    public void Per_platform_events_use_the_platform_argument()
    {
        Assert.Equal(new StreamerBotEventName("Twitch", "FirstWord"),
            StreamerBotEventMap.Map("users.firstWords", new Dictionary<string, object?> { ["platform"] = "Twitch" }));
        Assert.Equal(new StreamerBotEventName("YouTube", "FirstWords"),
            StreamerBotEventMap.Map("users.firstWords", new Dictionary<string, object?> { ["platform"] = "youtube" }));
        Assert.Equal(new StreamerBotEventName("Kick", "PresentViewers"),
            StreamerBotEventMap.Map("users.presentViewers", new Dictionary<string, object?> { ["platform"] = "kick" }));
    }

    [Fact]
    public void Envelope_has_timestamp_event_and_data()
    {
        var (name, message) = StreamerBotEnvelope.FromBotEvent(new BotEvent("obs.sceneChanged",
            new Dictionary<string, object?> { ["sceneName"] = "Live", ["count"] = 2, ["when"] = DateTimeOffset.UnixEpoch }));
        Assert.Equal("Obs.SceneChanged", name.ToString());
        Assert.Equal("Obs", message["event"]!["source"]!.GetValue<string>());
        Assert.Equal("SceneChanged", message["event"]!["type"]!.GetValue<string>());
        Assert.Equal("Live", message["data"]!["sceneName"]!.GetValue<string>());
        Assert.Equal(2, message["data"]!["count"]!.GetValue<int>());
        Assert.NotNull(message["data"]!["when"]);
        Assert.NotNull(message["timeStamp"]);
    }

    [Fact]
    public void Lan_addresses_are_downgraded_unless_allowed()
    {
        Assert.Equal(IPAddress.Loopback, NetworkPolicy.BindAddress("127.0.0.1", false, out var d1));
        Assert.False(d1);
        Assert.Equal(IPAddress.Loopback, NetworkPolicy.BindAddress("localhost", false, out _));
        Assert.Equal(IPAddress.Loopback, NetworkPolicy.BindAddress("0.0.0.0", false, out var d2));
        Assert.True(d2);
        Assert.Equal(IPAddress.Any, NetworkPolicy.BindAddress("0.0.0.0", true, out var d3));
        Assert.False(d3);
        Assert.Throws<FormatException>(() => NetworkPolicy.BindAddress("not an ip", true, out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("null", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1:7474", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("https://overlays.example", true)]
    [InlineData("http://overlays.example", false)]
    public void Origins(string? origin, bool allowed) =>
        Assert.Equal(allowed, NetworkPolicy.IsOriginAllowed(origin, ["https://overlays.example/"]));

    [Fact]
    public void Same_ip_host_is_allowed_but_names_are_not_trusted()
    {
        Assert.True(NetworkPolicy.IsOriginAllowed("http://192.168.1.5:7474", [], "192.168.1.5:8080"));
        Assert.False(NetworkPolicy.IsOriginAllowed("http://rebind.example:7474", [], "rebind.example:8080"));
        Assert.True(NetworkPolicy.IsOriginAllowed("https://anything.example", ["*"]));
    }

    [Fact]
    public void Static_files_resolve_inside_their_folder_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuebot-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<p>hi</p>");
        File.WriteAllText(Path.Combine(root, "sub", "a.css"), "body{}");
        var secret = Path.Combine(Path.GetTempPath(), "cuebot-secret-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "secret");
        try
        {
            HttpMapping[] mappings = [new() { Path = "/overlays", Folder = root }];
            Assert.Equal(Path.Combine(root, "index.html"), HttpApiServer.ResolveStaticFile(mappings, "/overlays"));
            Assert.Equal(Path.Combine(root, "index.html"), HttpApiServer.ResolveStaticFile(mappings, "/overlays/"));
            Assert.Equal(Path.Combine(root, "sub", "a.css"), HttpApiServer.ResolveStaticFile(mappings, "/overlays/sub/a.css"));
            Assert.Null(HttpApiServer.ResolveStaticFile(mappings, "/overlays/../" + Path.GetFileName(secret)));
            Assert.Null(HttpApiServer.ResolveStaticFile(mappings, "/overlays/%2e%2e/" + Path.GetFileName(secret)));
            Assert.Null(HttpApiServer.ResolveStaticFile(mappings, "/overlays/missing.js"));
            Assert.Null(HttpApiServer.ResolveStaticFile(mappings, "/other/index.html"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(secret);
        }
    }

    [Theory]
    [InlineData("/api/GetActions", "GetActions")]
    [InlineData("/GetActions", "GetActions")]
    [InlineData("/api/doaction", "DoAction")]
    [InlineData("/api/SendMessage", null)]
    [InlineData("/feed/chat", null)]
    [InlineData("/api/Nope", null)]
    public void Api_paths(string path, string? request) => Assert.Equal(request, HttpApiServer.ApiRequest(path));

    [Fact]
    public void Diff_reports_added_updated_and_deleted()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var changes = ServerManager.Diff(new() { [a] = "1", [b] = "1" }, new() { [a] = "1", [b] = "2", [c] = "1" }).ToList();
        Assert.Contains(("*Updated", b), changes);
        Assert.Contains(("*Added", c), changes);
        Assert.Equal(2, changes.Count);
        Assert.Equal([("*Deleted", a)], ServerManager.Diff(new() { [a] = "1" }, new()).ToList());
    }

    /// <summary>Event arguments typed DateTime (as in Streamer.bot's runs) still become ISO 8601 times in WebSocket payloads.</summary>
    [Fact]
    public void Payload_times_from_datetime_arguments_are_iso_8601()
    {
        var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var data = new System.Text.Json.Nodes.JsonObject();
        StreamerBotPayloads.Apply(new StreamerBotEventName("Twitch", "HypeTrainStart"), new Dictionary<string, object?>
        {
            ["hypeTrainId"] = "h1", ["startedAt"] = started, ["expiresAt"] = started.AddMinutes(5),
        }, data);
        Assert.Equal("2026-01-01T12:00:00.0000000Z", (string?)data["started_at"]);
        Assert.Equal("2026-01-01T12:05:00.0000000Z", (string?)data["expires_at"]);

        var shoutout = new System.Text.Json.Nodes.JsonObject();
        StreamerBotPayloads.Apply(new StreamerBotEventName("Twitch", "ShoutoutCreated"), new Dictionary<string, object?>
        {
            ["userId"] = "4", ["cooldownEndsAt"] = started,
        }, shoutout);
        Assert.Equal("2026-01-01T12:00:00.0000000Z", (string?)shoutout["cooldown_ends_at"]);
    }
}
