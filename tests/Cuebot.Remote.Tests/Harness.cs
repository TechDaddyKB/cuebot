using System.Collections.Concurrent;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server;

namespace Cuebot.Remote.Tests;

/// <summary>Records the arguments of the run it is part of.</summary>
internal sealed class CaptureSubAction : ISubActionHandler
{
    public const string Id = "test.capture";
    public ConcurrentQueue<Dictionary<string, object?>> Runs { get; } = new();
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Capture";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Runs.Enqueue(new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// One in-process Cuebot instance: real engine, local signals, a remote hub and the REST API on a random loopback port.
/// </summary>
internal sealed class Instance : IAsyncDisposable
{
    private Instance(string name, string token)
    {
        Token = token;
        Bus = new EventBus(Log);
        Profile = new ProfileService(null, Log);
        Globals = new GlobalVariables(null, Log);
        Hub = new RemoteHub(new RemoteSettings { InstanceName = name }, null, Bus, Log, Signals);
        Registry = new Registry(
            [new SendRemoteSignalSubAction(Hub), new WaitForRemoteSignalSubAction(Hub), new WaitForSignalSubAction(Signals), Capture],
            [], [new RemoteTriggers(Hub)]);
        Engine = new ActionEngine(Bus, Profile, Registry, Globals, Log);
        Engine.Start();
        Bus.Subscribe(e => Events.Enqueue(e));
    }

    public string Token { get; }
    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public ProfileService Profile { get; }
    public GlobalVariables Globals { get; }
    public SignalService Signals { get; } = new();
    public RemoteHub Hub { get; }
    public Registry Registry { get; }
    public ActionEngine Engine { get; }
    public CaptureSubAction Capture { get; } = new();
    public ServerManager? Manager { get; private set; }
    public ConcurrentQueue<BotEvent> Events { get; } = new();
    public int Port => Manager!.Api.Port;

    public BotEvent[] EventsOf(string type) => [.. Events.Where(e => e.Type == type)];

    public static async Task<Instance> StartAsync(string name, string token = "", int port = 0)
    {
        var instance = new Instance(name, token.Length > 0 ? token : "cb_test-" + name);
        await instance.StartServerAsync(port);
        return instance;
    }

    /// <summary>(Re)starts the REST API listener (port 0 = random).</summary>
    public async Task StartServerAsync(int port = 0)
    {
        var services = new ServerServices
        {
            Engine = Engine, Profile = Profile, Globals = Globals, Bus = Bus, Log = Log, Registry = Registry, WebSocketRoutes = [Hub],
        };
        Manager = new ServerManager(null, services);
        Manager.Settings.Api.Enabled = true;
        Manager.Settings.Api.Port = port;
        Manager.Settings.Api.Token = Token;
        await Manager.StartAsync();
    }

    public async Task StopServerAsync()
    {
        if (Manager is not null) await Manager.DisposeAsync();
        Manager = null;
    }

    /// <summary>Adds <paramref name="other"/> to remote.json under <paramref name="label"/> and connects.</summary>
    public async Task<RemoteInstanceDefinition> LinkToAsync(Instance other, string label, string? token = null, int reconnectSeconds = 1)
    {
        var definition = Hub.AddInstance(label);
        definition.Port = other.Port;
        definition.Token = token ?? other.Token;
        definition.ReconnectSeconds = reconnectSeconds;
        await Hub.ConnectAsync(definition);
        return definition;
    }

    public async Task<ActionDefinition> AddActionAsync(string name, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name, Concurrent = true };
        action.SubActions.AddRange(steps);
        await Profile.EditAsync(p => p.Actions.Add(action));
        return action;
    }

    public static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters) =>
        new() { Type = type, Parameters = parameters.ToDictionary(p => p.Key, p => p.Value) };

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
        await StopServerAsync();
        Engine.Dispose();
        Profile.Dispose();
        Globals.Dispose();
        Log.Dispose();
    }
}

internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting: " + because);
            await Task.Delay(20);
        }
    }
}
