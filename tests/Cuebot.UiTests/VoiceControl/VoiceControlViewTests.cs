using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuebot.App.ViewModels.VoiceControl;
using Cuebot.App.Views.VoiceControl;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Speech;
using Cuebot.VoiceControl;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Models;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.VoiceControl;

/// <summary>The Voice Control page renders its three tabs without binding errors in both themes (screenshots voice-*.png).</summary>
public sealed class VoiceControlViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-voice-").FullName;

    [AvaloniaFact]
    public async Task Shows_commands_settings_and_log_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<VoiceControlSettings>(Path.Combine(_dir, "voicecontrol.json"), log);
        settings.Value.Engine = "ui";
        var engine = new UiEngine();
        await using var voice = new VoiceControlService(settings, [new WhisperEngine(() => settings.Value, new ModelStore(_dir, new HttpClient(), log)), engine],
            new Mic(), new ModelStore(_dir, new HttpClient(), log), new EventBus(log), log);
        try
        {
            TestApp.BindingErrors.Clear();
            voice.AddCommand("Countdown", "start the countdown", CommandLocation.Start);
            var vm = new VoiceControlViewModel(voice);
            var view = new VoiceControlView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 900, Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush };
            window.Show();
            vm.Selected = vm.Commands[0];
            await vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.IsListening);
            engine.Session!.Hear("start the countdown now");
            UiPump.Pump();
            var tabs = view.FindControl<TabControl>("VoiceTabs")!;
            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                for (var i = 0; i < 3; i++)
                {
                    tabs.SelectedIndex = i;
                    UiPump.Pump();
                    UiPump.Save(window, $"voice-{i}-{name}");
                }
            }
            await vm.StopCommand.ExecuteAsync(null);
            vm.Engine = vm.Engines[0];
            tabs.SelectedIndex = 1;
            UiPump.Pump();
            UiPump.Save(window, "voice-whisper-light");
            Assert.Single(vm.Log);
            Assert.Contains("Countdown", vm.Log[0].Outcome);
            vm.Selected!.Phrase = "begin";
            Assert.Equal("begin", voice.Commands[0].Phrase);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Mic : IAudioInput
    {
        public IReadOnlyList<AudioDevice> GetInputDevices() => [new("Mic", "Mic", true)];
        public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb) => throw new NotSupportedException();
        public IAudioInputStream? CaptureSamples(string? deviceId, CaptureCallback onSamples) => new Stream();
        private sealed class Stream : IAudioInputStream { public int SampleRate => 16000; public void Dispose() { } }
    }

    private sealed class UiEngine : ISpeechRecognizerEngine
    {
        public string Id => "ui";
        public string DisplayName => "UI test engine";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool NeedsModel => false;
        public UiSession? Session { get; private set; }
        public IReadOnlyList<string> GetLocales() => ["en-US"];
        public Task<IRecognitionSession> StartAsync(RecognitionOptions options, CancellationToken ct) => Task.FromResult<IRecognitionSession>(Session = new UiSession());
    }

    private sealed class UiSession : IRecognitionSession
    {
        public int SampleRate => 16000;
        public string Description => "UI test engine";
        public event Action<RecognitionResult>? Recognized;
        public event Action<Exception>? Failed { add { } remove { } }
        public void Accept(ReadOnlySpan<float> samples) { }
        public void UpdateOptions(RecognitionOptions options) { }
        public void Hear(string text) => Recognized?.Invoke(new RecognitionResult(1, text, 0.9, true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
