using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuebot.App.ViewModels.Midi;
using Cuebot.App.Views.Midi;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Midi;
using Cuebot.Midi.Backends;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Midi;

/// <summary>
/// The MIDI tab lists MIDI In/Out devices from an in-memory backend, learns an event from an incoming message, shows the
/// monitor, and test-sends without binding errors in both themes (screenshots tests/Cuebot.UiTests/screenshots/midi-*.png).
/// </summary>
public sealed class MidiViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-midi-").FullName;

    [AvaloniaFact]
    public async Task Lists_devices_learns_an_event_and_sends_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<MidiSettings>(Path.Combine(_dir, "midi.json"), log);
        var backend = new LoopBackend();
        await using var midi = new MidiService(settings, new EventBus(log), log, backend);
        try
        {
            TestApp.BindingErrors.Clear();
            var pads = midi.AddDevice(MidiDirection.Input, new MidiDeviceConfig { Name = "Pads", Device = "nanoPAD2" });
            midi.AddDevice(MidiDirection.Output, new MidiDeviceConfig { Name = "Lights", Device = "APC mini" });
            await midi.StartAsync();
            var vm = new MidiViewModel(midi);
            var window = new Window
            {
                Content = new MidiView { DataContext = vm },
                Width = 1100,
                Height = 1500,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            await PumpUntil(() => vm.InputPorts.Count == 1 && vm.OutputPorts.Count == 1);

            Assert.Equal("Pads", Assert.Single(vm.Inputs).Name);
            Assert.True(vm.Inputs[0].IsOpen);
            Assert.Same(vm.Inputs[0], vm.SelectedInput);

            var learning = vm.LearnNewEventCommand.ExecuteAsync(null);
            await PumpUntil(() => vm.LearnStatus.Length > 0);
            backend.Emit(0xB0, 10, 64);
            await PumpUntil(() => learning.IsCompleted);
            var row = Assert.Single(vm.Events);
            Assert.Equal("ControlChange Pan (ch 1)", row.Name);
            Assert.Equal("ControlChange", row.Command);
            Assert.Equal(10, row.Data);
            Assert.Single(pads.Config.Events);
            backend.Emit(0xB0, 10, 1);
            await PumpUntil(() => vm.Monitor.Count == 2);
            Assert.Contains("-> ControlChange Pan (ch 1)", vm.Monitor[0]);

            vm.TestCommand = "ControlChange";
            vm.TestData1 = "7";
            vm.TestData2 = "100";
            await vm.SendTestCommand.ExecuteAsync(null);
            Assert.Equal("", vm.Error);
            Assert.Equal([0xB0, 7, 100], backend.Sent.Last());

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"midi-{name}");
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the MIDI page did not update");
            UiPump.Pump();
            await Task.Delay(20);
        }
        UiPump.Pump();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>One input (nanoPAD2) and one output (APC mini), in memory.</summary>
    private sealed class LoopBackend : IMidiBackend
    {
        private Action<MidiPacket>? _received;

        public List<byte[]> Sent { get; } = [];
        public string Name => "Test";
        public string? UnavailableReason => null;
        public event Action? PortsChanged { add { } remove { } }

        public void Emit(params byte[] bytes) => _received?.Invoke(new MidiPacket(bytes, 0));

        public IReadOnlyList<MidiPortInfo> ListPorts(MidiDirection direction) =>
            [new MidiPortInfo(direction == MidiDirection.Input ? "nanoPAD2" : "APC mini", direction)];

        public IMidiInputPort OpenInput(string name, Action<MidiPacket> received, Action<string> closed)
        {
            _received = received;
            return new Port(name, this);
        }

        public IMidiOutputPort OpenOutput(string name, Action<string> closed) => new Port(name, this);

        public void Dispose()
        {
        }

        private sealed class Port(string name, LoopBackend owner) : IMidiInputPort, IMidiOutputPort
        {
            public string Name => name;
            public void Send(ReadOnlySpan<byte> message) => owner.Sent.Add(message.ToArray());
            public void Dispose()
            {
            }
        }
    }
}
