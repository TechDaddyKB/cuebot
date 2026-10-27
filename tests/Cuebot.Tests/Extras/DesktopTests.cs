using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Desktop;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Desktop.DBus;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class HotkeyTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly FakeHotkeys _backend = new();
    private readonly EventRecorder _pressed;

    public HotkeyTests()
    {
        _bus = new EventBus(_log);
        _pressed = new EventRecorder(_bus, HotkeyEvents.Pressed);
    }

    [Theory]
    [InlineData("ctrl+shift+f1", "Ctrl+Shift+F1", "CTRL+SHIFT+F1")]
    [InlineData("Win + Alt + a", "Alt+Meta+A", "ALT+LOGO+a")]
    [InlineData("Control+D5", "Ctrl+5", "CTRL+5")]
    [InlineData("Ctrl++", "Ctrl+Equals", "CTRL+equal")]
    [InlineData("esc", "Escape", "Escape")]
    [InlineData("Shift+PgDn", "Shift+PageDown", "SHIFT+Next")]
    [InlineData("Ctrl+Num7", "Ctrl+NumPad7", "CTRL+KP_7")]
    [InlineData("MediaPlayPause", "MediaPlayPause", "XF86AudioPlay")]
    public void Gestures_parse_to_canonical_text(string input, string canonical, string portal)
    {
        var g = HotkeyGesture.Parse(input);
        Assert.Equal(canonical, g.ToString());
        Assert.Equal(portal, g.ToPortalTrigger());
        Assert.Equal(g, HotkeyGesture.Parse(g.ToString()));
    }

    [Theory]
    [InlineData("", "No key")]
    [InlineData("Ctrl+Hyper+A", "not a modifier")]
    [InlineData("Ctrl+Banana", "not a supported key")]
    public void Bad_gestures_explain_why(string input, string error)
    {
        Assert.False(HotkeyGesture.TryParse(input, out _, out var message));
        Assert.Contains(error, message);
    }

    [Fact]
    public void Every_key_maps_to_windows_and_x11()
    {
        foreach (var key in HotkeyKeys.Names)
        {
            Assert.NotNull(HotkeyKeys.WindowsVirtualKey(key));
            Assert.NotNull(HotkeyKeys.XKeysym(key));
            Assert.Equal(key, HotkeyKeys.Normalize(key.ToLowerInvariant()));
        }
        Assert.Equal(0x70, HotkeyKeys.WindowsVirtualKey("F1"));
        Assert.Equal(0x41, HotkeyKeys.WindowsVirtualKey("A"));
    }

    [Fact]
    public async Task Service_registers_enabled_valid_hotkeys_and_publishes_presses()
    {
        var ran = new List<(string Action, string Hotkey)>();
        using var service = new HotkeyService(null, _backend, _bus, _log, (action, args) =>
        {
            lock (ran) ran.Add((action, (string)args["hotkey"]!));
            return Task.CompletedTask;
        });
        var scene = new HotkeyDefinition { Name = "Scene", Gesture = "ctrl+f1", Action = "Switch scene" };
        var off = new HotkeyDefinition { Name = "Off", Gesture = "ctrl+f2", Enabled = false };
        var bad = new HotkeyDefinition { Name = "Bad", Gesture = "ctrl+nope" };
        var twin = new HotkeyDefinition { Name = "Twin", Gesture = "Control+F1" };
        var taken = new HotkeyDefinition { Name = "Taken", Gesture = "Alt+F4" };
        _backend.Refuse.Add("Alt+F4");
        foreach (var h in new[] { scene, off, bad, twin, taken }) await service.SaveAsync(h);
        Assert.Empty(_backend.Applied);   // nothing registered before start

        await service.StartAsync();
        Assert.Equal(["Ctrl+F1", "Alt+F4"], _backend.Applied[^1].Select(r => r.Gesture.ToString()));
        Assert.True(service.State(scene.Id)!.Registered);
        Assert.Equal("Disabled", service.State(off.Id)!.Message);
        Assert.Contains("not a supported key", service.State(bad.Id)!.Message);
        Assert.Contains("already used by another hotkey", service.State(twin.Id)!.Message);
        Assert.Equal("taken", service.State(taken.Id)!.Message);

        _backend.Press(scene.Id.ToString("N"));
        var e = Assert.Single(_pressed.Events);
        Assert.Equal(("Scene", "Ctrl+F1", scene.Id.ToString()), (e.Args["hotkeyName"], e.Args["hotkey"], e.Args["hotkeyId"]));
        await Task.Delay(50);
        Assert.Equal([("Switch scene", "Ctrl+F1")], ran);

        await service.RemoveAsync(taken.Id);
        Assert.Equal(["Ctrl+F1"], _backend.Applied[^1].Select(r => r.Gesture.ToString()));
        Assert.True(service.Simulate(off.Id));
        Assert.False(service.Simulate(Guid.NewGuid()));
        Assert.Equal(2, _pressed.Events.Count);
    }

    [Fact]
    public async Task Service_starts_on_the_first_timer_tick()
    {
        using var service = new HotkeyService(null, _backend, _bus, _log);
        await service.SaveAsync(new HotkeyDefinition { Name = "A", Gesture = "F9" });
        ((IPeriodicService)service).Tick(DateTimeOffset.Now);
        for (var i = 0; i < 100 && _backend.Applied.Count == 0; i++) await Task.Delay(10);
        Assert.Single(_backend.Applied);
        ((IPeriodicService)service).Tick(DateTimeOffset.Now);
        await Task.Delay(30);
        Assert.Single(_backend.Applied);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("scene", true)]
    [InlineData("control+F1", true)]
    [InlineData("Ctrl+F2", false)]
    [InlineData("Other", false)]
    public void Trigger_filters_by_name_or_combination(string filter, bool expected)
    {
        using var service = new HotkeyService(null, _backend, _bus, _log);
        var trigger = new DesktopTriggers(service).GetTriggers().Single(t => t.TypeId == "hotkeys.pressed");
        var e = new BotEvent(HotkeyEvents.Pressed, new Dictionary<string, object?> { ["hotkeyName"] = "Scene", ["hotkey"] = "Ctrl+F1", ["hotkeyId"] = "x" });
        var settings = ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["hotkey"] = filter });
        Assert.Equal(expected, trigger.Matches(e, new TriggerDefinition(), settings, new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task Unavailable_backend_reports_the_reason()
    {
        using var backend = new UnavailableGlobalHotkeys("no display");
        var results = await backend.ApplyAsync([new HotkeyRegistration("1", "x", HotkeyGesture.Parse("F1"))]);
        Assert.False(Assert.Single(results).Ok);
        Assert.Contains("no display", backend.Status);
    }

    [Fact]
    public async Task Default_backend_for_this_os_applies_an_empty_set_without_side_effects()
    {
        using var backend = GlobalHotkeys.CreateDefault(_log);
        Assert.Empty(await backend.ApplyAsync([]));
        Assert.Contains("No hotkeys", backend.Status);
    }

    public void Dispose() => _log.Dispose();

    /// <summary>Records applies; refuses chosen combinations; presses on demand.</summary>
    internal sealed class FakeHotkeys : IGlobalHotkeys
    {
        public List<IReadOnlyList<HotkeyRegistration>> Applied { get; } = [];
        public HashSet<string> Refuse { get; } = [];
        public string BackendName => "Fake";
        public string Status => "fake";
        public event Action<string>? Pressed;

        public Task<IReadOnlyList<HotkeyBindingResult>> ApplyAsync(IReadOnlyList<HotkeyRegistration> hotkeys, CancellationToken ct = default)
        {
            lock (Applied) Applied.Add(hotkeys);
            return Task.FromResult<IReadOnlyList<HotkeyBindingResult>>(hotkeys
                .Select(h => Refuse.Contains(h.Gesture.ToString()) ? new HotkeyBindingResult(h.Id, false, "taken") : new HotkeyBindingResult(h.Id, true))
                .ToArray());
        }

        public void Press(string id) => Pressed?.Invoke(id);
        public void Dispose() { }
    }
}

public sealed class DBusWireTests
{
    [Fact]
    public void Portal_call_round_trips()
    {
        var shortcuts = new List<object?>
        {
            new object?[] { "id1", new Dictionary<string, DBusVariant> { ["description"] = new("s", "Scene"), ["preferred_trigger"] = new("s", "CTRL+F1") } },
            new object?[] { "id2", new Dictionary<string, DBusVariant>() },
        };
        var call = DBusMessage.Call("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.GlobalShortcuts",
            "BindShortcuts", "oa(sa{sv})sa{sv}", "/org/freedesktop/portal/desktop/session/1_42/s1", shortcuts, "",
            new Dictionary<string, DBusVariant> { ["handle_token"] = new("s", "t1") });
        call.Serial = 7;
        var bytes = call.Serialize();
        Assert.Equal(bytes.Length, DBusMessage.TotalLength(bytes.AsSpan(0, 16)));

        var parsed = DBusMessage.Parse(bytes);
        Assert.Equal((DBusMessageType.MethodCall, 7u, "BindShortcuts", "oa(sa{sv})sa{sv}"), (parsed.Type, parsed.Serial, parsed.Member, parsed.Signature));
        Assert.Equal("/org/freedesktop/portal/desktop", parsed.Path);
        Assert.Equal("org.freedesktop.portal.Desktop", parsed.Destination);
        var list = Assert.IsType<List<object?>>(parsed.Body[1]);
        var first = Assert.IsType<object?[]>(list[0]);
        Assert.Equal("id1", first[0]);
        var props = Assert.IsType<Dictionary<object, object?>>(first[1]);
        Assert.Equal("CTRL+F1", ((DBusVariant)props["preferred_trigger"]!).Value);
        Assert.Empty(Assert.IsType<Dictionary<object, object?>>(((object?[])list[1]!)[1]));
        Assert.Equal("t1", ((DBusVariant)((Dictionary<object, object?>)parsed.Body[3]!)["handle_token"]!).Value);
    }

    [Fact]
    public void Scalars_and_alignment_round_trip()
    {
        var w = new DBusWriter();
        w.Write("y", (byte)1);
        w.Write("t", 1234567890123UL);   // padded to 8
        w.Write("b", true);
        w.Write("v", new DBusVariant("ad", new List<object?> { 1.5, -2.0 }));
        w.Write("n", (short)-3);
        w.Write("x", -5L);
        w.Write("g", "a{sv}");
        Assert.Equal(8, w.ToArray().Skip(1).TakeWhile(b => b == 0).Count() + 1);

        var r = new DBusReader(w.ToArray());
        Assert.Equal((byte)1, r.Read("y"));
        Assert.Equal(1234567890123UL, r.Read("t"));
        Assert.Equal(true, r.Read("b"));
        var v = Assert.IsType<DBusVariant>(r.Read("v"));
        Assert.Equal([1.5, -2.0], ((List<object?>)v.Value!).Cast<double>());
        Assert.Equal((short)-3, r.Read("n"));
        Assert.Equal(-5L, r.Read("x"));
        Assert.Equal("a{sv}", r.Read("g"));
    }

    [Fact]
    public void Signatures_split_and_addresses_parse()
    {
        Assert.Equal(["o", "a(sa{sv})", "s", "a{sv}"], DBusWire.SplitTypes("oa(sa{sv})sa{sv}"));
        Assert.Throws<FormatException>(() => DBusWire.SplitTypes("a(s"));
        Assert.NotNull(DBusConnection.ParseAddress("unix:path=/run/user/1000/bus"));
        Assert.NotNull(DBusConnection.ParseAddress("tcp:host=x;unix:abstract=/tmp/dbus-abc,guid=123"));
        Assert.Null(DBusConnection.ParseAddress("tcp:host=localhost,port=1"));
    }
}

public sealed class NotificationAndClipboardTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;

    public NotificationAndClipboardTests() => _bus = new EventBus(_log);

    [Fact]
    public async Task Auto_prefers_the_system_notifier_and_falls_back_to_the_in_app_toast()
    {
        var system = new FakeSystem { IsAvailable = true };
        var toast = new FakeToast { IsAvailable = true };
        var notifier = new DesktopNotifier(system, toast, _bus, _log);
        var n = new DesktopNotification("Hi", "There");

        Assert.Equal("system", await notifier.ShowAsync(n));
        Assert.Equal("in-app", await notifier.ShowAsync(n, NotificationTarget.InApp));
        system.IsAvailable = false;
        Assert.Equal("in-app", await notifier.ShowAsync(n));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifier.ShowAsync(n, NotificationTarget.System));
        toast.IsAvailable = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => notifier.ShowAsync(n));
        Assert.Single(system.Shown);
        Assert.Equal(2, toast.Shown.Count);
    }

    [Fact]
    public async Task Clicking_an_in_app_toast_fires_the_activation_trigger()
    {
        var toast = new FakeToast { IsAvailable = true };
        var recorder = new EventRecorder(_bus, DesktopEvents.ToastActivated);
        var notifier = new DesktopNotifier(new FakeSystem(), toast, _bus, _log);
        var result = await SubActionHarness.RunAsync(new ShowNotificationSubAction(notifier),
            new() { ["title"] = "Follow", ["message"] = "%user% followed!", ["id"] = "follow-toast", ["duration"] = "8" },
            new() { ["user"] = "Alice" });
        Assert.True(result.Success);
        Assert.Equal("in-app", result.Args["notificationShownWith"]);
        var shown = Assert.Single(toast.Shown);
        Assert.Equal(("Follow", "Alice followed!", 8), (shown.Notification.Title, shown.Notification.Message, shown.Notification.DurationSeconds));

        shown.Activated();
        Assert.Equal("follow-toast", Assert.Single(recorder.Events).Args["toastId"]);

        var empty = await SubActionHarness.RunAsync(new ShowNotificationSubAction(notifier), new() { ["title"] = "", ["message"] = "" });
        Assert.False(empty.Success);
    }

    [Fact]
    public void Notify_send_arguments_never_go_through_a_shell()
    {
        var args = NotifySendNotifier.Arguments(new DesktopNotification("-x; rm -rf ~", "$(boom)") { IconPath = "/tmp/i.png", DurationSeconds = 3 });
        Assert.Equal(["--app-name=Cuebot", "--expire-time=3000", "--icon=/tmp/i.png", "--", "-x; rm -rf ~", "$(boom)"], args);
        Assert.False(new NotifySendNotifier(_log, executable: null).IsAvailable && !OperatingSystem.IsLinux());
    }

    [Fact]
    public async Task Clipboard_sub_actions_use_the_service_and_explain_when_missing()
    {
        var clipboard = new FakeClipboard();
        var set = await SubActionHarness.RunAsync(new SetClipboardSubAction(clipboard), new() { ["text"] = "Hello %user%" }, new() { ["user"] = "Bob" });
        Assert.True(set.Success);
        Assert.Equal("Hello Bob", clipboard.Text);

        var get = await SubActionHarness.RunAsync(new GetClipboardSubAction(clipboard), new() { ["destination"] = "clip" });
        Assert.Equal(("Hello Bob", true), (get.Args["clip"], get.Args["clipFound"]));

        var none = await SubActionHarness.RunAsync(new GetClipboardSubAction(new NullClipboardService()), []);
        Assert.False(none.Success);
        Assert.Contains(none.Log, l => l.Message.Contains("clipboard is not available"));
    }

    [Fact]
    public async Task Clipboard_monitor_publishes_changes_when_enabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cuebot-desktop-{Guid.NewGuid():N}.json");
        using var settings = new JsonFile<DesktopSettings>(path, _log);
        var clipboard = new FakeClipboard { Text = "start" };
        var recorder = new EventRecorder(_bus, DesktopEvents.ClipboardChanged);
        var monitor = new ClipboardMonitor(() => clipboard, settings, _bus, _log);

        await monitor.PollAsync();
        clipboard.Text = "ignored while off";
        await monitor.PollAsync();
        settings.Value.WatchClipboard = true;
        await monitor.PollAsync();   // primes with the current text
        Assert.Empty(recorder.Events);
        clipboard.Text = "copied!";
        await monitor.PollAsync();
        await monitor.PollAsync();
        Assert.Equal("copied!", Assert.Single(recorder.Events).Args["clipboardText"]);
        File.Delete(path);
    }

    public void Dispose() => _log.Dispose();

    private sealed class FakeSystem : ISystemNotifier
    {
        public bool IsAvailable { get; set; }
        public List<DesktopNotification> Shown { get; } = [];
        public Task ShowAsync(DesktopNotification notification, CancellationToken ct)
        {
            Shown.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeToast : IToastPresenter
    {
        public bool IsAvailable { get; set; }
        public List<(DesktopNotification Notification, Action Activated)> Shown { get; } = [];
        public void Show(DesktopNotification notification, Action activated) => Shown.Add((notification, activated));
    }

    internal sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; set; }
        public bool IsAvailable => true;
        public Task SetTextAsync(string text, CancellationToken ct = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
        public Task<string?> GetTextAsync(CancellationToken ct = default) => Task.FromResult(Text);
    }
}

/// <summary>The wave-4 features plug into the real core composition without clashes or cycles.</summary>
public sealed class Wave4WiringTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-wave4-").FullName;

    [Fact]
    public async Task Core_plus_wave4_resolves_every_service_once()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddWave4Features();
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        T Get<T>() where T : notnull => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<T>(provider);

        var registry = Get<Registry>();
        foreach (var id in new[] { "quotes.add", "quotes.get", "quotes.edit", "quotes.delete", "quotes.count", "credits.reset", "credits.addCustom",
                     "credits.get", "points.add", "points.remove", "points.set", "points.get", "points.spend", "points.transfer",
                     "points.leaderboard", "points.resetAll", "desktop.showNotification", "desktop.setClipboard", "desktop.getClipboard" })
        {
            Assert.NotNull(registry.GetSubAction(id));
        }
        foreach (var id in new[] { "quotes.added", "quotes.shown", "quotes.removed", "quotes.updated", "points.thresholdCrossed", "hotkeys.pressed",
                     "desktop.toastActivated", "desktop.clipboardChanged" })
        {
            Assert.NotNull(registry.GetTrigger(id));
        }
        foreach (var handler in registry.SubActions.Where(h => h.Category is "Quotes" or "Credits" or "Points"))
        {
            Assert.NotEmpty(handler.DisplayName);
        }
        var api = Get<Core.Scripting.ScriptApi>();
        Assert.Contains(api.Functions, f => f.Name == "points.transfer");
        Assert.Contains(api.Functions, f => f.Name == "quotes.random");
        Assert.Contains(api.Functions, f => f.Name == "credits.get");
        Assert.Contains(api.Functions, f => f.Name == "desktop.notify");
        Assert.Same(Get<Core.Credits.CreditsCollector>(), Get<Core.Credits.ICreditsSource>());
        Assert.NotNull(Get<TimerService>());
        Assert.IsType<NullClipboardService>(Get<IClipboardService>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
