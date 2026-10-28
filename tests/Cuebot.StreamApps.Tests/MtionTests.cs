using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Mtion;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tests;

/// <summary>An in-memory mtion studio developer API (docs.mtion.xyz/mtion/developer-api/external-triggers).</summary>
internal sealed class FakeMtion : HttpMessageHandler
{
    public volatile bool Running = true;

    /// <summary>The clubhouse closes between the trigger list and the fire request (the fire then gets 404).</summary>
    public volatile bool ClubhouseClosesBeforeFire;
    public JsonObject? Active = Clubhouse("MCLB-1", "Kongo's Clubhouse");
    public ConcurrentQueue<(string Method, string Path, string Body)> Requests { get; } = new();
    public ConcurrentQueue<string> ContentTypes { get; } = new();
    public ConcurrentQueue<string> Hosts { get; } = new();

    public static JsonObject Clubhouse(string id, string name) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["description"] = "Tropical hide out",
        ["external_trigger_datas"] = new JsonArray(
            new JsonObject
            {
                ["id"] = "1ff84176fc8242d8918e1404f33d6161",
                ["name"] = "Confetti",
                ["output_parameters"] = new JsonArray(
                    new JsonObject { ["parameter_index"] = 0, ["name"] = "Text", ["data_type"] = "string" },
                    new JsonObject { ["parameter_index"] = 1, ["name"] = "Amount", ["data_type"] = "number" },
                    new JsonObject { ["parameter_index"] = 2, ["name"] = "Loud", ["data_type"] = "bool" },
                    new JsonObject { ["parameter_index"] = 3, ["name"] = "Color", ["data_type"] = "enum" }),
            },
            new JsonObject { ["id"] = "08195c6a468a49228ccf4c88f11558c7", ["name"] = "Wave", ["output_parameters"] = new JsonArray() }),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!Running) throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method.Method, path, body));
        if (request.Content?.Headers.ContentType is { } type) ContentTypes.Enqueue(type.ToString());
        Hosts.Enqueue(request.RequestUri.Authority);
        var active = Active;
        var last = path.Split('/')[^1];
        JsonNode? Trigger() => active?["external_trigger_datas"]?.AsArray().FirstOrDefault(t => (string?)t!["id"] == last)?.DeepClone();
        var clubhouses = new JsonArray(Clubhouse("MCLB-1", "Kongo's Clubhouse"), Clubhouse("MCLB-2", "Empty Template"));
        JsonNode? answer = path switch
        {
            "active-clubhouse" => active?.DeepClone(),
            "clubhouses" => clubhouses,
            _ when path.StartsWith("clubhouse/", StringComparison.Ordinal) => clubhouses.FirstOrDefault(c => (string?)c!["id"] == last)?.DeepClone(),
            "external-trigger/triggers" => active?["external_trigger_datas"]?.DeepClone(),
            _ when path.StartsWith("external-trigger/trigger/", StringComparison.Ordinal) => Trigger(),
            // Both documented ways to fire: PATCH with the JSON array as the body, or GET with ?parameter_data=[...].
            _ when path.StartsWith("external-trigger/fire-trigger/", StringComparison.Ordinal)
                   && (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Get) => ClubhouseClosesBeforeFire ? null : Trigger(),
            _ => null,
        };
        return answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

public sealed class MtionApiTests
{
    private static readonly MtionTrigger Confetti = MtionApi.ParseTrigger(JsonDocument.Parse(
        FakeMtion.Clubhouse("x", "y")["external_trigger_datas"]![0]!.ToJsonString()).RootElement);

    [Fact]
    public void Parses_triggers_and_clubhouses_from_the_documented_shapes()
    {
        Assert.Equal("Confetti", Confetti.Name);
        Assert.Equal(["string", "number", "bool", "enum"], Confetti.Parameters.Select(p => p.DataType));
        var clubhouse = MtionApi.ParseClubhouse(JsonDocument.Parse(FakeMtion.Clubhouse("MCLB-1", "Kongo").ToJsonString()).RootElement);
        Assert.Equal(("MCLB-1", "Kongo", 2), (clubhouse.Id, clubhouse.Name, clubhouse.Triggers.Count));
        Assert.Same(clubhouse.Triggers[1], MtionApi.Find(clubhouse.Triggers, "wave"));
        Assert.Same(clubhouse.Triggers[0], MtionApi.Find(clubhouse.Triggers, "1FF84176FC8242D8918E1404F33D6161"));
        Assert.Null(MtionApi.Find(clubhouse.Triggers, "nope"));
    }

    [Fact]
    public void Fire_body_converts_values_by_data_type_and_accepts_indexes()
    {
        var body = MtionApi.FireBody(Confetti, new Dictionary<string, string> { ["amount"] = "12", ["Text"] = "Hello", ["2"] = "yes", ["Color"] = "1" });
        Assert.Equal("""[{"parameter_index":0,"value":"Hello"},{"parameter_index":1,"value":12},{"parameter_index":2,"value":true},{"parameter_index":3,"value":1}]""", body);
        Assert.Contains("1.5", MtionApi.FireBody(Confetti, new Dictionary<string, string> { ["Amount"] = "1.5" }));
        Assert.Equal("[]", MtionApi.FireBody(Confetti, new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => MtionApi.FireBody(Confetti, new Dictionary<string, string> { ["Speed"] = "1" }));
        Assert.Throws<ArgumentException>(() => MtionApi.FireBody(Confetti, new Dictionary<string, string> { ["Amount"] = "lots" }));
        Assert.Throws<ArgumentException>(() => MtionApi.FireBody(Confetti, new Dictionary<string, string> { ["Color"] = "red" }));
    }
}

public sealed class MtionServiceTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private readonly FakeMtion _fake = new();
    private MtionService _mtion = null!;

    public Task InitializeAsync()
    {
        _mtion = new MtionService(_env.Settings, _env.Bus, _env.Log, new HttpClient(_fake), TestTimings.Fast, TimeSpan.FromMilliseconds(30));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _mtion.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Watches_mtion_start_clubhouse_changes_and_stop()
    {
        _fake.Running = false;
        await _mtion.ConnectAsync();
        await Wait.UntilAsync(() => _mtion.StatusMessage.Contains("not running"), "not running");
        Assert.Equal(ConnectionState.Connecting, _mtion.State);

        _fake.Running = true;
        await _env.WaitForEventAsync(MtionService.ConnectedEvent);
        var first = await _env.WaitForEventAsync(MtionService.ClubhouseChangedEvent);
        Assert.Equal("Kongo's Clubhouse", first.Args["mtion.clubhouse.name"]);
        await Wait.UntilAsync(() => _mtion.State == ConnectionState.Connected, "connected");
        Assert.Contains("Kongo's Clubhouse", _mtion.StatusMessage);
        Assert.Equal(2, _mtion.Clubhouse!.Triggers.Count);

        _fake.Active = FakeMtion.Clubhouse("MCLB-2", "Empty Template");
        var changed = await _env.WaitForEventAsync(MtionService.ClubhouseChangedEvent, e => (string?)e.Args["mtion.clubhouse.id"] == "MCLB-2");
        Assert.Equal("MCLB-1", changed.Args["mtion.oldClubhouse.id"]);

        _fake.Active = null;
        await Wait.UntilAsync(() => _mtion.StatusMessage.Contains("no clubhouse open"), "no clubhouse");

        _fake.Running = false;
        await _env.WaitForEventAsync(MtionService.DisconnectedEvent);
        Assert.False(_mtion.IsConnected);
        await _mtion.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _mtion.State);
    }

    [Fact]
    public async Task Sub_actions_set_Streamer_bot_variables()
    {
        await using var runner = new ActionRunner(MtionSubActions.Create(_mtion));

        var (ok, args) = await runner.RunAsync(("mtion.sendTrigger", new() { ["trigger"] = "confetti", ["parameters"] = "Text=Hi\nAmount=3" }));
        Assert.True(ok, runner.LastError);
        Assert.Equal(true, args["mtion.running"]);
        Assert.Equal(true, args["mtion.trigger.found"]);
        Assert.Equal(true, args["mtion.trigger.sent"]);
        var fire = _fake.Requests.Single(r => r.Method == "PATCH");
        Assert.Equal("external-trigger/fire-trigger/1ff84176fc8242d8918e1404f33d6161", fire.Path);
        Assert.Equal("""[{"parameter_index":0,"value":"Hi"},{"parameter_index":1,"value":3}]""", fire.Body);
        Assert.Equal("application/json", Assert.Single(_fake.ContentTypes));   // as in the docs' sample
        Assert.All(_fake.Hosts, h => Assert.Equal("localhost:35393", h));      // the docs' address
        var sent = await _env.WaitForEventAsync(MtionService.TriggerSentEvent);
        Assert.Equal("Confetti", sent.Args["mtion.trigger.name"]);

        (ok, args) = await runner.RunAsync(("mtion.sendTrigger", new() { ["trigger"] = "Missing" }));
        Assert.True(ok);
        Assert.Equal((true, false, false), ((bool)args["mtion.running"]!, (bool)args["mtion.trigger.found"]!, (bool)args["mtion.trigger.sent"]!));

        // A 404 from the fire request itself means "no clubhouse open or trigger not found": not found, not sent.
        _fake.ClubhouseClosesBeforeFire = true;
        (ok, args) = await runner.RunAsync(("mtion.sendTrigger", new() { ["trigger"] = "Wave" }));
        Assert.True(ok);
        Assert.Equal((true, false, false), ((bool)args["mtion.running"]!, (bool)args["mtion.trigger.found"]!, (bool)args["mtion.trigger.sent"]!));
        _fake.ClubhouseClosesBeforeFire = false;

        (ok, args) = await runner.RunAsync(("mtion.getActiveClubhouse", null));
        Assert.True(ok);
        Assert.Equal(true, args["mtion.clubhouse.found"]);
        Assert.Equal("MCLB-1", args["mtion.clubhouse.id"]);
        Assert.Equal("Tropical hide out", args["mtion.clubhouse.description"]);

        (ok, args) = await runner.RunAsync(("mtion.getTriggers", null));
        Assert.Equal(2, args["mtion.triggerCount"]);
        Assert.Equal("Confetti, Wave", args["mtion.triggerNames"]);

        _fake.Active = null;
        (ok, args) = await runner.RunAsync(("mtion.getActiveClubhouse", null));
        Assert.Equal((true, false), ((bool)args["mtion.running"]!, (bool)args["mtion.clubhouse.found"]!));

        _fake.Running = false;
        (ok, args) = await runner.RunAsync(("mtion.sendTrigger", new() { ["trigger"] = "Confetti" }));
        Assert.True(ok);
        Assert.Equal((false, false), ((bool)args["mtion.running"]!, (bool)args["mtion.trigger.sent"]!));

        _fake.Running = true;
        _fake.Active = FakeMtion.Clubhouse("MCLB-1", "Kongo");
        (ok, _) = await runner.RunAsync(("mtion.sendTrigger", new() { ["trigger"] = "Confetti", ["parameters"] = "Nope=1" }));
        Assert.False(ok);
        Assert.Contains("has no parameter 'Nope'", runner.LastError);
    }
}

public sealed class FogMtionObsbotRegistrationTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-streamapps-di2-").FullName;
    private readonly ServiceProvider _services;

    public FogMtionObsbotRegistrationTests() =>
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddStreamApps().BuildServiceProvider();

    [Fact]
    public async Task Registers_obsbot_and_mtion()
    {
        var statuses = _services.GetServices<IConnectionStatus>().Select(s => s.Name).ToArray();
        Assert.Contains("OBSBOT", statuses);
        Assert.Contains("mtion studio", statuses);

        var registry = _services.GetRequiredService<Cuebot.Core.Actions.Registry>();
        foreach (var id in ObsbotSubActions.TypeIds.Concat(MtionSubActions.TypeIds)) Assert.NotNull(registry.GetSubAction(id));
        foreach (var id in new[] { "obsbot.connected", "obsbot.zoomChanged", "obsbot.message", "mtion.connected", "mtion.clubhouseChanged", "mtion.triggerSent" })
            Assert.NotNull(registry.GetTrigger(id));

        var functions = _services.GetRequiredService<ScriptApi>().Functions.Select(f => f.Name).ToArray();
        foreach (var name in new[] { "obsbot.send", "obsbot.setZoom", "obsbot.getState", "mtion.sendTrigger", "mtion.getActiveClubhouse", "mtion.isRunning" })
            Assert.Contains(name, functions);

        var settings = _services.GetRequiredService<JsonFile<StreamAppsSettings>>().Value;
        Assert.Equal((16284, 57120, ObsbotTransport.Udp), (settings.Obsbot.Port, settings.Obsbot.ListenPort, settings.Obsbot.Transport));
        Assert.Equal(35393, settings.Mtion.Port);
        Assert.False(settings.Obsbot.AutoConnect || settings.Mtion.AutoConnect);
        await _services.GetRequiredService<ObsbotService>().StartAsync();
        await _services.GetRequiredService<MtionService>().StartAsync();
        Assert.False(_services.GetRequiredService<ObsbotService>().IsRunning);
        Assert.False(_services.GetRequiredService<MtionService>().IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
