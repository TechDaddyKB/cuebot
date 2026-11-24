using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Speech;
using Cuebot.Core;
using Cuebot.Speech;

namespace Cuebot.UiTests.Speech;

/// <summary>
/// The voice library card on Speech > Engines, with the real composition root: a Piper voice of Cuebot 1.0.6 moves
/// into the library at start-up, each format shows what it is, voices that can't speak yet say why, a voice copied into
/// the folder by hand shows up by itself, and nothing has binding errors (screenshot speech-voice-library.png).
/// </summary>
public sealed class VoiceLibraryPanelTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-voicelibrary-").FullName;
    private readonly AppHost _host;

    public VoiceLibraryPanelTests()
    {
        // Before start-up: a voice where Cuebot 1.0.6 kept them, and voices of each kind in the library.
        Model(Path.Combine(_dataDir, "piper", "voices", "en_US-lessac-low.onnx"));
        File.WriteAllText(Path.Combine(_dataDir, "piper", "voices", "en_US-lessac-low.onnx.json"),
            """{"audio":{"sample_rate":16000},"language":{"code":"en_US"},"phoneme_id_map":{"a":[1]}}""");
        var voices = Path.Combine(_dataDir, "voices");
        Model(Path.Combine(voices, "en_US-amy-medium", "en_US-amy-medium.onnx"));
        File.WriteAllText(Path.Combine(voices, "en_US-amy-medium", "en_US-amy-medium.onnx.json"),
            """{"audio":{"sample_rate":22050},"language":{"code":"en_US"},"vowel_clusters":[["a","ɪ"]],"phoneme_id_map":{"a":[1],"aɪ":[161]}}""");
        Model(Path.Combine(voices, "kokoro-en-v0_19", "model.onnx"),
            new Dictionary<string, string> { ["model_type"] = "kokoro", ["sample_rate"] = "24000", ["voice"] = "en-us", ["speaker_names"] = "af,am_adam" });
        File.WriteAllText(Path.Combine(voices, "kokoro-en-v0_19", "tokens.txt"), "$ 0\n");
        File.WriteAllBytes(Path.Combine(voices, "kokoro-en-v0_19", "voices.bin"), [0]);
        Directory.CreateDirectory(Path.Combine(voices, "half-copied"));
        File.WriteAllText(Path.Combine(voices, "half-copied", "readme.txt"), "x");
        _host = AppHost.Create(new AppPaths(_dataDir));
    }

    private static void Model(string path, IReadOnlyDictionary<string, string>? metadata = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x08, 0x07]);
        if (metadata is not null) OnnxMetadata.Append(path, metadata);
    }

    [AvaloniaFact]
    public void Shows_each_voice_with_what_it_is_and_why_it_cannot_speak_yet()
    {
        TestApp.BindingErrors.Clear();
        var vm = new SpeechViewModel(_host.Get<SpeechService>(), new FakeInput());
        var window = SpeechWindow.Show(vm, height: 1700);
        vm.SelectedTab = 1; // Engines
        UiPump.Pump();
        var panel = vm.Engines.Cards.Single(c => c.Id == "piper").Piper!;
        Assert.Equal(Path.Combine(_dataDir, "voices"), panel.FolderPath);

        var rows = panel.Installed.ToDictionary(r => r.Id);
        Assert.Equal(["en_US-amy-medium", "en_US-lessac-low", "half-copied", "kokoro-en-v0_19"], rows.Keys.Order());
        Assert.False(File.Exists(Path.Combine(_dataDir, "piper", "voices", "en_US-lessac-low.onnx"))); // moved in
        Assert.StartsWith("Piper voice (Piper 1.5 or later)", rows["en_US-amy-medium"].Detail);
        Assert.StartsWith("Kokoro voice", rows["kokoro-en-v0_19"].Detail);
        Assert.StartsWith("Piper voice", rows["en_US-lessac-low"].Detail);
        Assert.Contains("no voice model", rows["half-copied"].Note);
        if (VoiceEngine.CanInstall)
        {
            Assert.Contains("Get voice engine", rows["kokoro-en-v0_19"].Note);
            Assert.Equal("Get voice engine", panel.EngineButton);
        }
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "half-copied" && t.IsEffectivelyVisible);
        UiPump.Save(window, "speech-voice-library");

        // A voice copied in by hand appears without a restart.
        var glados = Path.Combine(_dataDir, "voices", "glados");
        Model(Path.Combine(glados, "en_US-glados.onnx"), new Dictionary<string, string>
        {
            ["model_type"] = "vits", ["comment"] = "piper", ["voice"] = "en-us", ["n_speakers"] = "1", ["sample_rate"] = "22050",
        });
        File.WriteAllText(Path.Combine(glados, "tokens.txt"), "a 1\n");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!panel.Installed.Any(r => r.Id == "glados") && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            UiPump.Pump();
        }
        Assert.StartsWith("sherpa-onnx voice", panel.Installed.Single(r => r.Id == "glados").Detail);

        panel.Installed.Single(r => r.Id == "half-copied").DeleteCommand.Execute(null);
        UiPump.Pump();
        Assert.DoesNotContain(panel.Installed, r => r.Id == "half-copied");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
