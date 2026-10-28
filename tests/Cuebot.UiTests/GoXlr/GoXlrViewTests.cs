using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.GoXlr;
using Cuebot.App.Views.GoXlr;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.GoXlr;
using Cuebot.GoXlr.Protocol;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.GoXlr;

/// <summary>
/// The GoXLR tab: settings write through, and once connected to a scripted GoXLR Utility it shows the faders (volume,
/// mute), profiles and effects, drives them, and renders without binding errors in both themes
/// (screenshots tests/Cuebot.UiTests/screenshots/goxlr-*.png).
/// </summary>
public sealed class GoXlrViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-goxlr-").FullName;

    [AvaloniaFact]
    public async Task Shows_the_device_and_drives_it_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<GoXlrSettings>(Path.Combine(_dir, "goxlr.json"), log);
        var utility = new ScriptedUtility();
        await using var goXlr = new GoXlrService(settings, new EventBus(log), log, () => new ScriptedSocket(utility),
            GoXlrTimings.Default with { ReconnectDelay = TimeSpan.FromMilliseconds(50) });
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new GoXlrViewModel(goXlr);
            var window = new Window
            {
                Content = new GoXlrView { DataContext = vm },
                Width = 1000,
                Height = 1100,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();
            Assert.True(vm.HasNoDevice);
            Assert.Equal("—", vm.Detected);
            UiPump.Save(window, "goxlr-disconnected");

            vm.Host = " 127.0.0.1 ";
            vm.AutoConnect = true;
            Assert.Equal("127.0.0.1", settings.Value.Host);
            Assert.True(settings.Value.AutoConnect);

            await vm.ConnectCommand.ExecuteAsync(null);
            await Until(() => vm.IsConnected && vm.Faders.Count == 4, "device");
            Assert.Contains("GoXLR Utility 1.2.3", vm.Detected);
            Assert.Equal("Music", vm.Faders[1].ChannelName);
            Assert.Equal(75, vm.Faders[1].Volume);
            Assert.Equal(4, vm.Effects.Count);
            Assert.Equal(new[] { "Default", "Gaming" }, vm.Profiles);
            Assert.Contains("Profile: Default", vm.DeviceSummary);

            await vm.Faders[1].ToggleMuteCommand.ExecuteAsync(null);
            Assert.Equal("Fader B muted.", vm.Info);
            await Until(() => vm.Faders[1].Muted && vm.Faders[1].MuteLabel == "Unmute", "mute shown");
            vm.Faders[0].Volume = 40;
            await Until(() => utility.Commands.Any(c => c.Contains("\"SetVolume\":[\"Mic\",102]", StringComparison.Ordinal)), "volume sent");
            await vm.Effects[0].ToggleCommand.ExecuteAsync(null);
            Assert.Contains(utility.Commands, c => c.Contains("\"SetFXEnabled\":true", StringComparison.Ordinal));
            vm.SelectedProfile = "Gaming";
            await vm.LoadProfileCommand.ExecuteAsync(null);
            Assert.Contains(utility.Commands, c => c.Contains("\"LoadProfile\":[\"Gaming\",true]", StringComparison.Ordinal));
            await vm.ToggleCoughCommand.ExecuteAsync(null);
            Assert.Equal("Mic muted.", vm.Info);

            UiPump.Pump();
            var faders = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "GoXlrFaders");
            Assert.Equal(4, faders.ItemCount);
            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"goxlr-{name}");
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
            UiPump.Pump();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* settings flush may still hold the file briefly */ }
    }

    /// <summary>A GoXLR Utility answering GetStatus and applying fader-mute commands (with a status patch), "Ok" otherwise.</summary>
    private sealed class ScriptedUtility
    {
        public const string Serial = "S210500000UI";
        public ConcurrentQueue<string> Commands { get; } = new();

        public JsonNode Status { get; } = JsonNode.Parse($$"""
            {
              "config": { "daemon_version": "1.2.3" },
              "mixers": {
                "{{Serial}}": {
                  "hardware": { "device_type": "Full", "serial_number": "{{Serial}}" },
                  "fader_status": {
                    "A": { "channel": "Mic", "mute_type": "All", "mute_state": "Unmuted", "scribble": null },
                    "B": { "channel": "Music", "mute_type": "All", "mute_state": "Unmuted", "scribble": null },
                    "C": { "channel": "Chat", "mute_type": "All", "mute_state": "Unmuted", "scribble": null },
                    "D": { "channel": "System", "mute_type": "ToStream", "mute_state": "Unmuted", "scribble": null }
                  },
                  "levels": { "volumes": { "Mic": 255, "Music": 191, "Chat": 230, "System": 128, "Game": 255, "Headphones": 200 } },
                  "cough_button": { "is_toggle": false, "mute_type": "All", "state": "Unmuted" },
                  "effects": {
                    "is_enabled": false, "active_preset": "Preset1",
                    "preset_names": { "Preset1": "Default", "Preset2": "Spooky" },
                    "current": { "megaphone": { "is_enabled": false }, "robot": { "is_enabled": false }, "hard_tune": { "is_enabled": true } }
                  },
                  "sampler": { "active_bank": "A", "banks": {} },
                  "button_down": {}, "router": {},
                  "profile_name": "Default", "mic_profile_name": "DEFAULT",
                  "mic_status": { "mic_type": "Dynamic" }
                }
              },
              "files": { "profiles": ["Default", "Gaming"], "mic_profiles": ["DEFAULT"], "presets": [], "samples": {} }
            }
            """)!;

        public IEnumerable<string> Answer(JsonNode request)
        {
            var id = request["id"]!.GetValue<ulong>();
            var data = request["data"]!;
            if (data is JsonValue v)
            {
                yield return v.GetValue<string>() == "GetStatus"
                    ? new JsonObject { ["id"] = id, ["data"] = new JsonObject { ["Status"] = Status.DeepClone() } }.ToJsonString()
                    : $$"""{"id":{{id}},"data":"Ok"}""";
                yield break;
            }
            var command = data["Command"]![1]!.AsObject();
            Commands.Enqueue(command.ToJsonString());
            yield return $$"""{"id":{{id}},"data":"Ok"}""";
            var (name, args) = command.First();
            string? path = null;
            JsonNode? value = null;
            if (name == "SetFaderMuteState")
            {
                path = $"/mixers/{Serial}/fader_status/{args![0]!.GetValue<string>()}/mute_state";
                value = args[1]!.GetValue<string>();
            }
            else if (name == "SetCoughMuteState")
            {
                path = $"/mixers/{Serial}/cough_button/state";
                value = args!.GetValue<string>();
            }
            if (path is not null)
            {
                var patch = new JsonArray(new JsonObject { ["op"] = "replace", ["path"] = path, ["value"] = value });
                JsonPatchTarget(path, value);
                yield return new JsonObject { ["id"] = ulong.MaxValue, ["data"] = new JsonObject { ["Patch"] = patch } }.ToJsonString();
            }
        }

        private void JsonPatchTarget(string path, JsonNode? value)
        {
            var parts = path.Trim('/').Split('/');
            var node = Status;
            foreach (var p in parts[..^1]) node = node[p]!;
            node[parts[^1]] = value?.DeepClone();
        }
    }

    private sealed class ScriptedSocket(ScriptedUtility utility) : IGoXlrTransport
    {
        private readonly Channel<string?> _inbox = Channel.CreateUnbounded<string?>();

        public string? CloseReason => null;

        public Task ConnectAsync(Uri url, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string text, CancellationToken ct)
        {
            foreach (var reply in utility.Answer(JsonNode.Parse(text)!)) _inbox.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _inbox.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
