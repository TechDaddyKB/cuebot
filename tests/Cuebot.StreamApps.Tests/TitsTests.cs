using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Tits;
using Cuebot.StreamApps.Tests.Support;

namespace Cuebot.StreamApps.Tests;

public sealed class TitsProtocolTests
{
    [Fact]
    public void Requests_carry_the_api_header_and_data()
    {
        using var doc = JsonDocument.Parse(TitsProtocol.Request(TitsProtocol.ThrowItems, "r1", TitsProtocol.ThrowData(["a", "b"], 0.1, 5, true)));
        var root = doc.RootElement;
        Assert.Equal("TITSPublicApi", root.GetProperty("apiName").GetString());
        Assert.Equal("1.0", root.GetProperty("apiVersion").GetString());
        Assert.Equal("r1", root.GetProperty("requestID").GetString());
        Assert.Equal("TITSThrowItemsRequest", root.GetProperty("messageType").GetString());
        var data = root.GetProperty("data");
        Assert.Equal(["a", "b"], data.GetProperty("items").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0.1, data.GetProperty("delayTime").GetDouble());
        Assert.Equal(5, data.GetProperty("amountOfThrows").GetInt32());
        Assert.True(data.GetProperty("errorOnMissingID").GetBoolean());

        using var list = JsonDocument.Parse(TitsProtocol.Request(TitsProtocol.TriggerList, "r2"));
        Assert.False(list.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Hit_event_becomes_streamer_bot_variables()
    {
        var hit = JsonDocument.Parse("""
            {"apiName":"TITSPublicApi","timestamp":133173178563183532,"apiVersion":"1.0","requestID":"x","messageType":"TITSHitEvent",
             "data":{"itemID":"d1a7","itemName":"apple","triggerID":"t1","triggerName":"Throw Bananas","strength":24.5,
                     "direction":{"x":0.3261459,"y":0.9256075,"z":0.1920408}}}
            """).RootElement;
        var args = TitsProtocol.EventArgs(hit);
        Assert.Equal("d1a7", args["itemId"]);
        Assert.Equal("apple", args["itemName"]);
        Assert.Equal("t1", args["triggerId"]);
        Assert.Equal("Throw Bananas", args["triggerName"]);
        Assert.Equal(24.5, args["strength"]);
        Assert.Equal(0.9256075, args["direction.y"]);
        Assert.Equal(["a", "b c", "d"], TitsProtocol.SplitList("a, b c\nd,,"));
    }
}

public sealed class TitsServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private TitsService _tits = null!;
    private FakeTits _fake = null!;

    public async Task InitializeAsync()
    {
        _tits = new TitsService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        _fake = new FakeTits(_env.Server);
        await _tits.ConnectAsync();
        await _env.WaitForEventAsync(TitsEvents.Connected);
        await Wait.UntilAsync(() => _tits.Items.Count == 2 && _tits.Triggers.Count == 2, "lists");
    }

    public async Task DisposeAsync()
    {
        await _tits.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Connects_both_sockets_and_caches_items_and_triggers()
    {
        Assert.True(_tits.IsConnected);
        Assert.True(_tits.EventsConnected);
        Assert.Contains(_env.Server.Attempts, u => u.ToString() == "ws://127.0.0.1:42069/websocket");
        Assert.Contains(_env.Server.Attempts, u => u.ToString() == "ws://127.0.0.1:42069/events");
        Assert.Single(_env.Events(TitsEvents.WebsocketConnected));
        Assert.Single(_env.Events(TitsEvents.EventsConnected));
        Assert.Equal(["apple", "banana"], _tits.Items.Select(i => i.Name));
        Assert.Equal("ebb7", _tits.Triggers.Single(t => t.Name == "Throw Bananas").Id);
        Assert.Contains("2 items, 2 triggers", _tits.StatusMessage);
        var itemList = _fake.Requests.First(r => r.Str("messageType") == "TITSItemListRequest");
        Assert.False(itemList.GetProperty("data").GetProperty("sendImage").GetBoolean());
        Assert.False(itemList.GetProperty("sendImage").GetBoolean());   // the obsolete top-level copy, for older builds
        Assert.False(_fake.Requests.First(r => r.Str("messageType") == "TITSTriggerListRequest").TryGetProperty("sendImage", out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Throws_items_by_name_and_id_and_activates_triggers()
    {
        Assert.Equal(3, await _tits.ThrowItemsByNameAsync(["Apple", "16d7"], 0.2, 3));
        var request = _fake.Requests.Last(r => r.Str("messageType") == "TITSThrowItemsRequest").GetProperty("data");
        Assert.Equal(["60c6", "16d7"], request.GetProperty("items").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0.2, request.GetProperty("delayTime").GetDouble());
        Assert.Equal(3, request.GetProperty("amountOfThrows").GetInt32());

        var unknown = await Assert.ThrowsAsync<StreamAppRequestException>(() => _tits.ThrowItemsByNameAsync(["pear"]));
        Assert.Contains("pear", unknown.Message);

        await _tits.ActivateTriggerAsync("throw bananas");
        var byId = _fake.Requests.Last(r => r.Str("messageType") == "TITSTriggerActivateRequest").GetProperty("data");
        Assert.Equal("ebb7", byId.GetProperty("triggerID").GetString());

        await _tits.ActivateTriggerByNameAsync("New Follow");
        var byName = _fake.Requests.Last(r => r.Str("messageType") == "TITSTriggerActivateRequest").GetProperty("data");
        Assert.Equal("6709", byName.GetProperty("triggerID").GetString());   // from the cached list
        Assert.Equal("New Follow", byName.GetProperty("triggerName").GetString());

        // Text that is neither a cached id nor a cached name goes in both fields.
        await Assert.ThrowsAsync<StreamAppRequestException>(() => _tits.ActivateTriggerAsync("f00d"));
        var raw = _fake.Requests.Last(r => r.Str("messageType") == "TITSTriggerActivateRequest").GetProperty("data");
        Assert.Equal("f00d", raw.GetProperty("triggerID").GetString());
        Assert.Equal("f00d", raw.GetProperty("triggerName").GetString());
    }

    [Fact]
    public async Task Builds_before_the_name_fallback_still_activate_by_name_fill_event_names_and_fail_fast_on_errors()
    {
        _fake.Legacy = true;
        await _tits.ActivateTriggerByNameAsync("throw bananas");
        Assert.Equal("ebb7", _fake.Requests.Last(r => r.Str("messageType") == "TITSTriggerActivateRequest").GetProperty("data").GetProperty("triggerID").GetString());

        // An error without the requestID fails the one waiting request at once (well inside the timeout).
        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<StreamAppRequestException>(() => _tits.ActivateTriggerByNameAsync("Nope"));
        Assert.Contains("No trigger found", error.Message);
        Assert.True(DateTime.UtcNow - started < TestTimings.Fast.RequestTimeout);

        // Trigger events from those builds carry only the id: the name comes from the cached list.
        _fake.Events!.Push("""{"apiName":"TITSPublicApi","apiVersion":"1.0","timestamp":133173178563183532,"requestID":"someID","messageType":"TITSTriggerActivatedEvent","data":{"triggerID":"6709"}}""");
        var activated = await _env.WaitForEventAsync(TitsEvents.TriggerActivated);
        Assert.Equal("New Follow", activated.Args["triggerName"]);
        _fake.Events.Push("""{"apiName":"TITSPublicApi","apiVersion":"1.0","timestamp":133173178563183532,"requestID":"someID","messageType":"TITSHitEvent","data":{"itemID":"16d7","strength":3.5,"direction":{"x":0,"y":1,"z":0}}}""");
        var hit = await _env.WaitForEventAsync(TitsEvents.ItemHit);
        Assert.Equal("banana", hit.Args["itemName"]);
        Assert.Equal("", hit.Args["triggerName"]);
    }

    [Fact]
    public async Task Error_responses_fail_the_request()
    {
        _fake.FailNext = true;
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => _tits.ActivateTriggerByNameAsync("Nope"));
        Assert.Contains("No trigger found", ex.Message);
    }

    [Fact]
    public async Task Broadcasts_become_trigger_events_and_filters_match()
    {
        _fake.Events!.Push("""
            {"apiName":"TITSPublicApi","apiVersion":"1.0","requestID":"b","messageType":"TITSHitEvent",
             "data":{"itemID":"60c6","itemName":"apple","triggerID":"ebb7","triggerName":"Throw Bananas","strength":24.5,"direction":{"x":1,"y":0,"z":0}}}
            """);
        _fake.Events.Push("""{"apiName":"TITSPublicApi","apiVersion":"1.0","timestamp":133173178563183532,"requestID":"someID","messageType":"TITSTriggerActivatedEvent","data":{"triggerID":"6709","triggerName":"New Follow"}}""");
        _fake.Events.Push("""{"apiName":"TITSPublicApi","apiVersion":"1.0","timestamp":133173178563183532,"requestID":"someID","messageType":"TITSTriggerEndedEvent","data":{"triggerID":"6709","triggerName":"New Follow"}}""");
        var hit = await _env.WaitForEventAsync(TitsEvents.ItemHit);
        var activated = await _env.WaitForEventAsync(TitsEvents.TriggerActivated);
        await _env.WaitForEventAsync(TitsEvents.TriggerEnded);
        Assert.Equal("apple", hit.Args["itemName"]);
        Assert.Equal(24.5, hit.Args["strength"]);
        Assert.Equal(1.0, hit.Args["direction.x"]);
        Assert.Equal("6709", activated.Args["triggerId"]);

        var triggers = new TitsTriggers(_tits).GetTriggers().ToDictionary(t => t.TypeId);
        Assert.True(Matches(triggers[TitsEvents.ItemHit], hit, new() { ["item"] = "Apple" }));
        Assert.True(Matches(triggers[TitsEvents.ItemHit], hit, new() { ["minStrength"] = "20" }));
        Assert.False(Matches(triggers[TitsEvents.ItemHit], hit, new() { ["minStrength"] = "30" }));
        Assert.False(Matches(triggers[TitsEvents.ItemHit], hit, new() { ["trigger"] = "New Follow" }));
        Assert.True(Matches(triggers[TitsEvents.TriggerActivated], activated, new() { ["trigger"] = "new follow" }));
        Assert.All(triggers.Values, t => Assert.Equal("T.I.T.S.", t.Category));
    }

    [Fact]
    public async Task Events_socket_reconnects_on_its_own()
    {
        _fake.Events!.Close();
        await _env.WaitForEventAsync(TitsEvents.EventsDisconnected);
        await _env.WaitForEventAsync(TitsEvents.Disconnected);
        Assert.True(_tits.IsConnected);   // the request socket is still up
        await Wait.UntilAsync(() => _env.Events(TitsEvents.EventsConnected).Length == 2, "events reconnect");
        await Wait.UntilAsync(() => _env.Events(TitsEvents.Connected).Length == 2, "connected again");

        _fake.Data!.Close();
        await _env.WaitForEventAsync(TitsEvents.WebsocketDisconnected);
        await Wait.UntilAsync(() => _env.Events(TitsEvents.WebsocketConnected).Length == 2, "request socket reconnect");
    }

    [Fact]
    public async Task Sub_actions_throw_and_store_the_count()
    {
        var handlers = TitsSubActions.Create(_tits);
        Assert.Equal(TitsSubActions.TypeIds.Order(), handlers.Select(h => h.TypeId).Order());
        var (ok, args, error) = await ActionHarness.RunAsync(handlers,
            ("tits.throwItemsByName", new() { ["items"] = "apple\nbanana", ["amount"] = "4" }),
            ("tits.getItems", new()),
            ("tits.activateTrigger", new() { ["trigger"] = "New Follow" }));
        Assert.True(ok, error);
        Assert.Equal(4, args["tits.thrownItems"]);
        Assert.Equal(2, args["tits.itemCount"]);
        Assert.Contains("apple", (string)args["tits.items"]!);
        Assert.Equal("6709", _fake.Requests.Last(r => r.Str("messageType") == "TITSTriggerActivateRequest").GetProperty("data").GetProperty("triggerID").GetString());
    }

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string> settings) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings), new Dictionary<string, object?>());

    /// <summary>
    /// T.I.T.S.'s API as the README documents it: answers item/trigger lists (with <c>encodedImage</c> and the
    /// top-level <c>triggerCount</c>), throws and activations (no data); /events only broadcasts.
    /// </summary>
    private sealed class FakeTits
    {
        private readonly ConcurrentQueue<JsonElement> _requests = new();

        public FakeTits(FakeServer server) => _ = Task.Run(async () =>
        {
            while (true)
            {
                FakeSocket socket;
                try { socket = await server.NextAsync(); }
                catch (OperationCanceledException) { continue; }
                if (socket.Url!.AbsolutePath == "/events") Events = socket;
                else
                {
                    Data = socket;
                    _ = Task.Run(() => ServeAsync(socket));
                }
            }
        });

        public FakeSocket? Data { get; private set; }
        public FakeSocket? Events { get; private set; }
        public bool FailNext { get; set; }

        /// <summary>
        /// Behave like a build from before the README's October 2025 update: activation reads triggerID only, and
        /// (undocumented, as some clients report) errors come back without the requestID.
        /// </summary>
        public bool Legacy { get; set; }

        public static readonly (string Id, string Name)[] Triggers = [("6709", "New Follow"), ("ebb7", "Throw Bananas")];
        public IReadOnlyList<JsonElement> Requests => _requests.ToArray();

        private async Task ServeAsync(FakeSocket socket)
        {
            while (!socket.Disposed)
            {
                string text;
                try { text = await socket.NextSentAsync(); }
                catch (OperationCanceledException) { continue; }
                var request = JsonDocument.Parse(text).RootElement.Clone();
                _requests.Enqueue(request);
                var type = request.GetProperty("messageType").GetString()!;
                JsonNode? data = type switch
                {
                    "TITSItemListRequest" => new JsonObject
                    {
                        ["items"] = new JsonArray(
                            new JsonObject { ["ID"] = "60c6", ["name"] = "apple", ["encodedImage"] = "" },
                            new JsonObject { ["ID"] = "16d7", ["name"] = "banana", ["encodedImage"] = "" }),
                    },
                    "TITSTriggerListRequest" => new JsonObject
                    {
                        ["triggers"] = new JsonArray(new JsonObject { ["ID"] = "6709", ["name"] = "New Follow" }, new JsonObject { ["ID"] = "ebb7", ["name"] = "Throw Bananas" }),
                    },
                    "TITSThrowItemsRequest" => new JsonObject { ["numberOfThrownItems"] = request.GetProperty("data").GetProperty("amountOfThrows").GetInt32() },
                    _ => null,
                };
                var failing = FailNext && type == "TITSTriggerActivateRequest";
                if (failing) FailNext = false;
                if (type == "TITSTriggerActivateRequest" && !failing)
                {
                    // Look up triggerID; the current README falls back to triggerName, legacy builds do not.
                    var activate = request.GetProperty("data");
                    var found = Triggers.Any(t => t.Id == activate.Str("triggerID"))
                                || (!Legacy && Triggers.Any(t => t.Name == activate.Str("triggerName")));
                    failing = !found;
                }
                var response = new JsonObject
                {
                    ["apiName"] = "TITSPublicApi",
                    ["timestamp"] = 133173178563183532,
                    ["apiVersion"] = "1.0",
                    ["requestID"] = request.GetProperty("requestID").GetString(),
                    ["messageType"] = failing ? "APIError" : type.Replace("Request", "Response"),
                };
                if (failing) response["data"] = new JsonObject { ["errorID"] = 1, ["message"] = "No trigger found" };
                else if (data is not null) response["data"] = data;
                // The README's trigger list response also counts the triggers outside data.
                if (type == "TITSTriggerListRequest" && !failing) response["triggerCount"] = 2;
                if (failing && Legacy) response.Remove("requestID");
                socket.Push(response.ToJsonString());
            }
        }
    }
}

internal static class JsonTestExtensions
{
    public static string Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
