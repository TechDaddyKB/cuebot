using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuebot.App.ViewModels.Controllers;
using Cuebot.App.Views.Controllers;
using Cuebot.Controllers;
using Cuebot.Controllers.Device;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Controllers;

/// <summary>
/// The Controllers page shows a (fake) gamepad and joystick with live buttons and axes, learns an input, and renders
/// without binding errors in both themes (screenshots tests/Cuebot.UiTests/screenshots/controllers-*.png). No SDL.
/// </summary>
public sealed class ControllersViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-controllers-").FullName;

    [AvaloniaFact]
    public async Task Shows_controllers_live_state_and_learns_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<ControllerSettings>(Path.Combine(_dir, "controllers.json"), log);
        var backend = new Backend();
        await using var service = new ControllerService(backend, settings, new EventBus(log), log, new AppPaths(_dir), () => false);
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new ControllersViewModel(service);
            var window = new Window
            {
                Content = new ControllersView { DataContext = vm },
                Width = 1000,
                Height = 1600,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();   // attaching the view holds controller input open
            await WaitAsync(() => service.IsRunning);
            UiPump.Pump();
            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal("#1 DualSense Wireless Controller", vm.Rows[0].Name);

            backend.Raise(new ButtonEvent(1, 0, true));
            backend.Raise(new AxisEvent(1, 0, 24000));
            backend.Raise(new HatEvent(2, 0, 1));
            vm.RefreshLive();
            UiPump.Pump();
            Assert.True(vm.Rows[0].Buttons.Single(b => b.Name == "South").IsPressed);
            Assert.Equal("South (Cross)", vm.Rows[0].Buttons[0].Text);
            Assert.True(vm.Rows[0].Axes[0].Value > 0.5);
            Assert.Equal("Held: South", vm.Rows[0].Held);
            Assert.Equal("Hat1: Up", vm.Rows[1].Hats);

            var learn = vm.LearnCommand.ExecuteAsync(null);
            Assert.True(vm.IsLearning);
            backend.Raise(new ButtonEvent(1, 3, true));
            await learn;
            Assert.Equal("Button North (Triangle) on #1 DualSense Wireless Controller", vm.LearnText);
            Assert.Contains("button = North", vm.LearnSettings);

            vm.StickDeadzone = 20;
            Assert.Equal(0.2, settings.Value.StickDeadzone, 3);

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"controllers-{name}");
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            UiPump.Pump();
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A DualSense and a flight stick that are "plugged in" when input starts.</summary>
    private sealed class Backend : IControllerBackend
    {
        public string Name => "Fake";
        public event Action<ControllerRawEvent>? EventReceived;
        public event Action<string>? Faulted { add { } remove { } }

        public Task<ControllerBackendStart> StartAsync(ControllerBackendOptions options)
        {
            Raise(new DeviceAddedEvent(new ControllerDeviceInfo
            {
                Id = 1, Name = "DualSense Wireless Controller", Guid = "050000004c050000e60c000000000000", IsGamepad = true, Type = "PS5",
                Buttons = 26, Axes = 6, CanRumble = true, HasRgbLed = true, Power = ControllerPower.OnBattery, BatteryPercent = 60,
                VendorId = 0x054c, ProductId = 0x0ce6,
            }));
            Raise(new DeviceAddedEvent(new ControllerDeviceInfo
            {
                Id = 2, Name = "Thrustmaster T.16000M", Guid = "030000004f0400000ab1000000000000", Type = "Joystick", Buttons = 16, Axes = 4, Hats = 1,
            }));
            return Task.FromResult(new ControllerBackendStart(true, "Fake SDL", null));
        }

        public void Stop() { }
        public Task<bool> RumbleAsync(uint id, ushort low, ushort high, uint durationMs) => Task.FromResult(true);
        public Task<bool> SetLedAsync(uint id, byte red, byte green, byte blue) => Task.FromResult(true);
        public Task<int> AddMappingsAsync(string file) => Task.FromResult(0);
        public void Raise(ControllerRawEvent e) => EventReceived?.Invoke(e);
        public void Dispose() { }
    }
}
