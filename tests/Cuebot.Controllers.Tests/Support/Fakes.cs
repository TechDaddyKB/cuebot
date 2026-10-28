using System.Collections.Concurrent;
using Cuebot.Controllers.Device;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Controllers.Tests.Support;

/// <summary>A device layer driven by the test: no SDL, no devices.</summary>
internal sealed class FakeBackend : IControllerBackend
{
    public string Name => "Fake";
    public event Action<ControllerRawEvent>? EventReceived;
    public event Action<string>? Faulted;

    /// <summary>Devices "plugged in" before start; reported during <see cref="StartAsync"/> like SDL does.</summary>
    public List<ControllerDeviceInfo> Present { get; } = [];

    public string? FailWith { get; set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool Running { get; private set; }
    public ControllerBackendOptions? Options { get; private set; }
    public ConcurrentQueue<(uint Id, ushort Low, ushort High, uint Ms)> Rumbles { get; } = new();
    public ConcurrentQueue<(uint Id, byte R, byte G, byte B)> Leds { get; } = new();
    public List<string> MappingFiles { get; } = [];

    public Task<ControllerBackendStart> StartAsync(ControllerBackendOptions options)
    {
        Starts++;
        Options = options;
        if (FailWith is not null) return Task.FromResult(new ControllerBackendStart(false, "", FailWith));
        Running = true;
        foreach (var device in Present) Raise(new DeviceAddedEvent(device));
        return Task.FromResult(new ControllerBackendStart(true, "Fake 1.0", null, options.MappingsFile is null ? 0 : 3));
    }

    public void Stop()
    {
        if (Running) Stops++;
        Running = false;
    }

    public Task<bool> RumbleAsync(uint id, ushort low, ushort high, uint durationMs)
    {
        Rumbles.Enqueue((id, low, high, durationMs));
        return Task.FromResult(Running);
    }

    public Task<bool> SetLedAsync(uint id, byte red, byte green, byte blue)
    {
        Leds.Enqueue((id, red, green, blue));
        return Task.FromResult(Running && id != 99);
    }

    public Task<int> AddMappingsAsync(string file)
    {
        MappingFiles.Add(file);
        return Task.FromResult(2);
    }

    public void Raise(ControllerRawEvent e) => EventReceived?.Invoke(e);

    public void Fault(string reason)
    {
        Running = false;
        Faulted?.Invoke(reason);
    }

    public void Dispose() => Stop();

    public static ControllerDeviceInfo Xbox(uint id = 1, string name = "Xbox Series X Controller") => new()
    {
        Id = id, Name = name, Guid = "030000005e040000130b000000000000", IsGamepad = true, Type = "Xbox One",
        Buttons = 26, Axes = 6, CanRumble = true, VendorId = 0x045e, ProductId = 0x0b13,
    };

    public static ControllerDeviceInfo DualSense(uint id = 2) => new()
    {
        Id = id, Name = "DualSense Wireless Controller", Guid = "050000004c050000e60c000000000000", IsGamepad = true, Type = "PS5",
        Buttons = 26, Axes = 6, CanRumble = true, HasRgbLed = true, Power = ControllerPower.OnBattery, BatteryPercent = 70,
    };

    public static ControllerDeviceInfo SwitchPro(uint id = 3) => new()
    {
        Id = id, Name = "Nintendo Switch Pro Controller", Guid = "050000007e0500000920000000000000", IsGamepad = true, Type = "Switch Pro",
        Buttons = 26, Axes = 6,
    };

    public static ControllerDeviceInfo FlightStick(uint id = 4) => new()
    {
        Id = id, Name = "Thrustmaster T.16000M", Guid = "030000004f0400000ab1000000000000", IsGamepad = false,
        Type = "Joystick", Buttons = 16, Axes = 4, Hats = 1,
    };
}

/// <summary>A clock the test moves by hand; timers it creates never fire (tests flush explicitly).</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A service wired to a fake backend, a real bus and a temp data folder.</summary>
internal sealed class Env : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-controllers-").FullName;

    public Env(bool triggers = true, ControllerWatchMode mode = ControllerWatchMode.Automatic)
    {
        Log = new AppLog();
        Bus = new EventBus(Log);
        Bus.Subscribe(Events.Enqueue);
        Paths = new AppPaths(_dir);
        Settings = new JsonFile<ControllerSettings>(Path.Combine(_dir, "controllers.json"), Log);
        Settings.Value.Mode = mode;
        TriggersWanted = triggers;
        Service = new ControllerService(Backend, Settings, Bus, Log, Paths, () => TriggersWanted, Time);
    }

    public FakeBackend Backend { get; } = new();
    public ManualTime Time { get; } = new();
    public AppLog Log { get; }
    public EventBus Bus { get; }
    public AppPaths Paths { get; }
    public JsonFile<ControllerSettings> Settings { get; }
    public ControllerService Service { get; }
    public bool TriggersWanted { get; set; }
    public ConcurrentQueue<BotEvent> Events { get; } = new();

    public IReadOnlyList<BotEvent> Of(string type) => Events.Where(e => e.Type == type).ToArray();

    /// <summary>Starts the service and waits until the backend reported its devices.</summary>
    public async Task StartAsync()
    {
        await Service.EnsureRunningAsync();
        Assert.True(Service.IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
