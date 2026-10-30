using Cuebot.Core.Actions;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Desktop.Input;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Tests.Commands;
using System.Runtime.Versioning;

namespace Cuebot.Tests.Extras;

/// <summary>Key-combo parsing and translation (platform code stays thin) and the mouse click service.</summary>
public sealed class InputTests : IDisposable
{
    private readonly AppLog _log = new();

    private static string Text(string keys) => string.Join(" | ", KeyCombos.Parse(keys));

    [Theory]
    [InlineData("Ctrl+Shift+F1", "Ctrl+Shift+F1")]
    [InlineData("ctrl+c, ctrl+v", "Ctrl+C | Ctrl+V")]
    [InlineData("Ctrl+C,Ctrl+V", "Ctrl+C | Ctrl+V")]
    [InlineData("Alt+Tab Enter", "Alt+Tab | Enter")]
    [InlineData("Ctrl+,", "Ctrl+Comma")]
    [InlineData("Ctrl+, Esc", "Ctrl+Comma | Escape")]
    [InlineData("MediaPlayPause", "MediaPlayPause")]
    [InlineData("Win+Shift+S", "Shift+Meta+S")]
    public void Cuebot_syntax(string input, string expected) => Assert.Equal(expected, Text(input));

    [Theory]
    [InlineData("^c", "Ctrl+C")]
    [InlineData("^+{F1}", "Ctrl+Shift+F1")]
    [InlineData("%{TAB}", "Alt+Tab")]
    [InlineData("{ENTER 3}", "Enter | Enter | Enter")]
    [InlineData("Hi!~", "Shift+H | I | Shift+1 | Enter")]
    [InlineData("+(ab)", "Shift+A | Shift+B")]
    [InlineData("{+}{{}", "Shift+Equals | Shift+BracketLeft")]
    [InlineData("{BS}{DEL}{PGDN}", "Backspace | Delete | PageDown")]
    [InlineData("a b!", "A | Space | B | Shift+1")]
    public void SendKeys_syntax(string input, string expected) => Assert.Equal(expected, Text(input));

    [Theory]
    [InlineData("", "No keys given")]
    [InlineData("Ctrl+Nope", "'Nope' is not a supported key")]
    [InlineData("{NOPE}", "'{NOPE}' is not a supported key")]
    [InlineData("^", "not followed by a key")]
    [InlineData("{ENTER 5000}", "Too many keys")]
    [InlineData("€", "is not a supported key")]
    public void Bad_keys_explain_why(string input, string error)
    {
        Assert.False(KeyCombos.TryParse(input, out _, out var message));
        Assert.Contains(error, message);
        Assert.Throws<FormatException>(() => KeyCombos.Parse(input));
    }

    [Fact]
    public void Events_press_modifiers_first_and_release_them_last()
    {
        var events = KeyEvent.For(HotkeyGesture.Parse("Ctrl+Shift+Alt+A"));
        Assert.Equal(["Ctrl+", "Alt+", "Shift+", "A+", "A-", "Shift-", "Alt-", "Ctrl-"],
            events.Select(e => e.Key + (e.Down ? "+" : "-")));
        Assert.True(events[0].IsModifier);
        Assert.False(events[3].IsModifier);
        Assert.Equal(2, KeyEvent.For(HotkeyGesture.Parse("F5")).Count);
    }

    [Theory]
    [InlineData("A", 0x41, false)]
    [InlineData("5", 0x35, false)]
    [InlineData("F1", 0x70, false)]
    [InlineData("F24", 0x87, false)]
    [InlineData("Enter", 0x0D, false)]
    [InlineData("Left", 0x25, true)]
    [InlineData("Delete", 0x2E, true)]
    [InlineData("Divide", 0x6F, true)]
    [InlineData("NumPad3", 0x63, false)]
    [InlineData("Ctrl", 0x11, false)]
    [InlineData("Meta", 0x5B, true)]
    [InlineData("MediaPlayPause", 0xB3, false)]
    public void Windows_virtual_keys(string key, int vk, bool extended) => Assert.Equal(((ushort)vk, extended), KeyCodes.Windows(key));

    [Theory]
    [InlineData("A", 0x61)]
    [InlineData("Z", 0x7a)]
    [InlineData("0", 0x30)]
    [InlineData("F1", 0xffbe)]
    [InlineData("F12", 0xffc9)]
    [InlineData("NumPad7", 0xffb7)]
    [InlineData("Enter", 0xff0d)]
    [InlineData("Space", 0x20)]
    [InlineData("PageDown", 0xff56)]
    [InlineData("Delete", 0xffff)]
    [InlineData("Equals", 0x3d)]
    [InlineData("Backquote", 0x60)]
    [InlineData("VolumeUp", 0x1008ff13)]
    [InlineData("Ctrl", 0xffe3)]
    [InlineData("Shift", 0xffe1)]
    [InlineData("Alt", 0xffe9)]
    [InlineData("Meta", 0xffeb)]
    public void X11_keysyms(string key, int keysym) => Assert.Equal(keysym, KeyCodes.Keysym(key));

    [Fact]
    public void Every_hotkey_key_can_be_sent_on_both_platforms()
    {
        foreach (var key in HotkeyKeys.Names)
        {
            Assert.True(KeyCodes.Windows(key) is not null, key);
            Assert.True(KeyCodes.Keysym(key) is not null, key);
        }
    }

    [Fact]
    public async Task Output_base_sends_each_combo_in_order()
    {
        using var output = new RecordingKeyboard();
        await output.PressAsync("Ctrl+C, Ctrl+V");
        Assert.Equal(["Ctrl+ C+ C- Ctrl-", "Ctrl+ V+ V- Ctrl-"], output.Combos);
        await Assert.ThrowsAsync<FormatException>(() => output.PressAsync("Ctrl+Nope"));
        using var none = new UnavailableKeyboardOutput("no display");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => none.PressAsync("A"));
        Assert.Contains("no display", ex.Message);
    }

    [Fact]
    public async Task Keyboard_press_sub_action()
    {
        using var output = new RecordingKeyboard();
        var ok = await SubActionHarness.RunAsync(new KeyboardPressSubAction(output), new() { ["keys"] = "^c{F5}", ["delay"] = "0" });
        Assert.True(ok.Success);
        Assert.Equal("Ctrl+C, F5", ok.Args["keysPressed"]);
        Assert.Equal(2, output.Combos.Count);
        var bad = await SubActionHarness.RunAsync(new KeyboardPressSubAction(output), new() { ["keys"] = "Ctrl+Nope" });
        Assert.False(bad.Success);
        Assert.Equal(2, output.Combos.Count);
    }

    [Fact]
    public void Mouse_click_trigger_filters_button_and_modifiers()
    {
        var args = new Dictionary<string, object?> { ["mouseButton"] = "Left", ["modifiers"] = "Ctrl+Shift" };
        Assert.True(MouseClickService.Matches("Any", "", args));
        Assert.True(MouseClickService.Matches("left", "", args));
        Assert.False(MouseClickService.Matches("Right", "", args));
        Assert.True(MouseClickService.Matches("Left", "shift+ctrl", args));
        Assert.False(MouseClickService.Matches("Left", "Ctrl", args));
        Assert.False(MouseClickService.Matches("Left", "None", args));
        Assert.True(MouseClickService.Matches("Left", "None", new Dictionary<string, object?> { ["mouseButton"] = "Left", ["modifiers"] = "" }));
        Assert.False(MouseClickService.Matches("Left", "Bogus", args));
        Assert.Equal("Ctrl+Alt", MouseClickService.ModifierText(HotkeyModifiers.Alt | HotkeyModifiers.Ctrl));
    }

    [Fact]
    public void Mouse_hook_runs_only_while_a_trigger_uses_it_and_publishes_clicks()
    {
        var bus = new EventBus(_log);
        var recorder = new EventRecorder(bus, InputEvents.MouseClick);
        var source = new FakeMouse();
        var wanted = false;
        using var service = new MouseClickService(source, bus, _log, () => wanted);
        service.Sync();
        Assert.Equal(0, source.Starts);
        wanted = true;
        service.Sync();
        service.Sync();
        Assert.Equal(1, source.Starts);
        Assert.True(service.IsRunning);

        source.Click(new MouseClick(MouseButton.Right, 10, 20, HotkeyModifiers.Ctrl));
        var e = Assert.Single(recorder.Events);
        Assert.Equal(("Right", 10, 20, "Ctrl"), (e.Args["mouseButton"], e.Args["mouseX"], e.Args["mouseY"], e.Args["modifiers"]));
        Assert.Equal(("Right", 1, 10, 20), (e.Args["mouse.button"], e.Args["mouse.clicks"], e.Args["mouse.x"], e.Args["mouse.y"]));

        wanted = false;
        service.Sync();
        Assert.Equal(1, source.Stops);
        Assert.False(service.IsRunning);

        var unavailable = new MouseClickService(new UnavailableMouseClickSource(MouseClickSources.WaylandReason), bus, _log, () => true);
        unavailable.Sync();
        Assert.False(unavailable.IsRunning);
        Assert.Contains("Wayland", unavailable.Status);
    }

    [Fact]
    public void Profile_check_finds_enabled_mouse_triggers()
    {
        ProfileSnapshot With(bool actionEnabled, bool triggerEnabled) => new(
        [
            new ActionDefinition
            {
                Enabled = actionEnabled,
                Triggers = [new TriggerDefinition { Type = InputFeatures.MouseClickTriggerId, Enabled = triggerEnabled }],
            },
        ], []);
        Assert.True(InputFeatures.UsesMouseClicks(With(true, true)));
        Assert.False(InputFeatures.UsesMouseClicks(With(false, true)));
        Assert.False(InputFeatures.UsesMouseClicks(With(true, false)));
        Assert.False(InputFeatures.UsesMouseClicks(new ProfileSnapshot([], [])));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void X11_mouse_probes_survive_many_threads()
    {
        // libXi destroys its process-wide state when the last XInput display closes; unserialized this crashed the
        // test host (SIGSEGV in XCloseDisplay, or heap corruption) when parallel tests probed the mouse source.
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("DISPLAY") is not { Length: > 0 }) return;
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            for (var n = 0; n < 25; n++)
            {
                var (source, _) = X11MouseClickSource.TryCreate(_log);
                if (source is null || n % 5 != i % 5) continue;
                source.Start();
                source.Stop();
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
    }

    public void Dispose() => _log.Dispose();

    private sealed class RecordingKeyboard : KeyboardOutputBase
    {
        public List<string> Combos { get; } = [];
        public override string BackendName => "Test";
        public override string Status => "Ready";

        public RecordingKeyboard() => ComboDelay = TimeSpan.Zero;

        protected override Task SendAsync(IReadOnlyList<KeyEvent> events, CancellationToken ct)
        {
            Combos.Add(string.Join(' ', events.Select(e => e.Key + (e.Down ? "+" : "-"))));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMouse : IMouseClickSource
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public string BackendName => "Fake";
        public string Status => "ok";
        public event Action<MouseClick>? Clicked;
        public bool Start() { Starts++; return true; }
        public void Stop() => Stops++;
        public void Click(MouseClick c) => Clicked?.Invoke(c);
        public void Dispose() { }
    }
}
