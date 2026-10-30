using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Speech;

namespace Cuebot.UiTests.Speech;

internal sealed class FakeEngine(string id, string name, Func<bool> available, IReadOnlyList<VoiceInfo> voices) : ITtsEngine
{
    public int VoiceCalls { get; private set; }
    public string Id => id;
    public string DisplayName => name;
    public bool IsAvailable => available();
    public string? UnavailableReason => IsAvailable ? null : $"{name} needs credentials.";

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        VoiceCalls++;
        return Task.FromResult(voices);
    }

    public Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest request, CancellationToken ct) =>
        Task.FromResult(SynthesizedAudio.Wav([]));
}

internal sealed class FakePlayer : IAudioPlayer
{
    public Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<AudioDevice> GetOutputDevices() =>
        [new AudioDevice("spk", "Speakers", true), new AudioDevice("vcable", "Virtual cable", false)];
}

internal sealed class FakeInput : IAudioInput
{
    public int Monitors { get; private set; }

    public IReadOnlyList<AudioDevice> GetInputDevices() =>
        [new AudioDevice("mic", "Desk microphone", true), new AudioDevice("headset", "Headset", false)];

    public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb)
    {
        Monitors++;
        onLevelDb(-20);
        return new Stop();
    }

    private sealed class Stop : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>A speech service over fake engines and devices, persisting to a temp speech.json.</summary>
internal sealed class SpeechHarness : IAsyncDisposable
{
    public SpeechHarness(Action<SpeechSettings>? seed = null)
    {
        Dir = Directory.CreateTempSubdirectory("cuebot-ui-speech-").FullName;
        Log = new AppLog();
        File = new JsonFile<SpeechSettings>(SettingsPath, Log);
        seed?.Invoke(File.Value);
        var settings = File.Value;
        Local = new FakeEngine("fake", "Fake voices", () => true,
        [
            new VoiceInfo("v1", "Alice", "en-US") { Gender = "Female", Tier = "Neural", Styles = ["cheerful", "sad"] },
            new VoiceInfo("v2", "Bob", "en-GB") { Gender = "Male" },
        ]);
        Cloud = new FakeEngine("azure", "Microsoft Azure", () => settings.Engines.Azure.Key.Length > 0,
            [new VoiceInfo("en-US-JennyNeural", "Jenny", "en-US") { Styles = ["whispering"] }]);
        Input = new FakeInput();
        Speech = new SpeechService(File, [Local, Cloud], new FakePlayer(), new EventBus(Log), Log, audioInput: Input);
        ViewModel = new SpeechViewModel(Speech, Input);
    }

    public string Dir { get; }
    public string SettingsPath => Path.Combine(Dir, "speech.json");
    public AppLog Log { get; }
    public JsonFile<SpeechSettings> File { get; }
    public FakeEngine Local { get; }
    public FakeEngine Cloud { get; }
    public FakeInput Input { get; }
    public SpeechService Speech { get; }
    public SpeechViewModel ViewModel { get; }

    /// <summary>Writes pending saves and reads speech.json back as a fresh object.</summary>
    public SpeechSettings Reload()
    {
        File.Flush();
        using var reread = new JsonFile<SpeechSettings>(SettingsPath, new AppLog());
        return reread.Value;
    }

    public async ValueTask DisposeAsync()
    {
        ViewModel.Gate.Dispose();
        await Speech.DisposeAsync();
        File.Dispose();
        try { Directory.Delete(Dir, recursive: true); }
        catch (IOException) { }
    }
}

internal static class UiPump
{
    public static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static void Save(Window window, string name)
    {
        Pump();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, name + ".png"));
    }
}

internal static class SpeechWindow
{
    /// <summary>A window like the main one: Speech page on the theme's panel background.</summary>
    public static Window Show(object viewModel, double height = 1300)
    {
        var window = new Window
        {
            Content = new Cuebot.App.Views.SpeechView { DataContext = viewModel },
            Width = 1360,
            Height = height,
            Background = Avalonia.Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }
}
