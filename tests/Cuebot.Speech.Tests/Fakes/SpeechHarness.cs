using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>A started <see cref="SpeechService"/> wired to fakes, with settings in a temporary folder.</summary>
internal sealed class SpeechHarness : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cuebot-speech-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonFile<SpeechSettings> _settings;

    public SpeechHarness(params ITtsEngine[] engines)
    {
        Directory.CreateDirectory(_directory);
        _settings = new JsonFile<SpeechSettings>(Path.Combine(_directory, "speech.json"), Log);
        Engine = engines.OfType<FakeTtsEngine>().FirstOrDefault() ?? new FakeTtsEngine(voices: "default");
        Bus = new EventBus(Log);
        Service = new SpeechService(_settings, engines.Length == 0 ? [Engine] : engines, Player, Bus, Log);
        Settings.Aliases.Add(new VoiceAlias { Name = "Main", EngineId = Engine.Id, VoiceId = "v1" });
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public FakeTtsEngine Engine { get; }
    public FakeAudioPlayer Player { get; } = new();
    public SpeechService Service { get; }
    public SpeechSettings Settings => Service.Settings;

    public static async Task<SpeechHarness> StartAsync(params ITtsEngine[] engines)
    {
        var harness = new SpeechHarness(engines);
        await harness.Service.StartAsync();
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        _settings.Dispose();
        Log.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
