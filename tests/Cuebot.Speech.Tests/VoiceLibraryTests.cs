using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using ICSharpCode.SharpZipLib.BZip2;

namespace Cuebot.Speech.Tests;

/// <summary>Builds small stand-in voice files: real ONNX framing (so metadata reads work), no real model.</summary>
internal static class FakeVoices
{
    /// <summary>A minimal ONNX ModelProto: ir_version, a 300-byte "graph" to skip, and the given metadata.</summary>
    public static void Model(string path, IReadOnlyDictionary<string, string>? metadata = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        {
            file.Write([0x08, 0x07]);                 // field 1 (ir_version) = 7
            file.Write([0x3A, 0xAC, 0x02]);           // field 7 (graph), 300 bytes
            file.Write(new byte[300]);
        }
        if (metadata is not null) OnnxMetadata.Append(path, metadata);
    }

    public const string ClassicConfig = """
        {"audio":{"sample_rate":16000},"espeak":{"voice":"en-us"},"language":{"code":"en_US"},"phoneme_type":"espeak",
         "speaker_id_map":{},"phoneme_id_map":{"_":[0],"^":[1],"$":[2]," ":[3],"a":[14],"ɪ":[74]}}
        """;

    // A cut-down Piper 1.5 voice config that uses vowel clusters, as en_US-amy-medium does since November 2026.
    public const string ClusterConfig = """
        {"audio":{"sample_rate":22050},"espeak":{"voice":"en-us"},"language":{"code":"en_US"},"num_speakers":1,"speaker_id_map":{},
         "piper_version":"1.5.0","vowel_clusters":[["a","ɪ"],["o","ʊ"]],
         "phoneme_id_map":{"_":[0],"a":[14],"ɪ":[74],"o":[27],"ʊ":[100],"aɪ":[161],"oʊ":[165]}}
        """;

    // Lists multi-character phonemes without using them (no vowel_clusters), as it_IT-serena-medium does.
    public const string ListsClustersConfig = """
        {"audio":{"sample_rate":22050},"language":{"code":"it_IT"},"num_speakers":1,"speaker_id_map":{},"piper_version":"1.5.0",
         "phoneme_type":"espeak",
         "phoneme_id_map":{"_":[0],"a":[14],"ɪ":[74],"aɪ":[161],"oʊ":[165],"ˈ":[120]},
         "phoneme_map":{"ɐ":["a"],"ʧ":["t","ʃ"],"ts":["t"]},
         "inference":{"noise_scale":0.5,"phoneme_silence":{".":0.2,"aɪ":0.1}}}
        """;

    public static string Piper(string folder, string name, string config)
    {
        Model(Path.Combine(folder, name + ".onnx"));
        File.WriteAllText(Path.Combine(folder, name + ".onnx.json"), config);
        return folder;
    }

    public static string SherpaVits(string folder, string name = "en_US-glados-high")
    {
        Model(Path.Combine(folder, name + ".onnx"), new Dictionary<string, string>
        {
            ["model_type"] = "vits", ["comment"] = "piper", ["language"] = "English", ["voice"] = "en-us",
            ["has_espeak"] = "1", ["n_speakers"] = "1", ["sample_rate"] = "22050",
        });
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "_ 0\n^ 1\n$ 2\n  3\na 14\n");
        Directory.CreateDirectory(Path.Combine(folder, "espeak-ng-data"));
        File.WriteAllText(Path.Combine(folder, "espeak-ng-data", "phontab"), "x");
        return folder;
    }

    public static string Kokoro(string folder, string speakers = "af,af_bella,am_adam")
    {
        Model(Path.Combine(folder, "model.onnx"), new Dictionary<string, string>
        {
            ["model_type"] = "kokoro", ["sample_rate"] = "24000", ["voice"] = "en-us", ["speaker_names"] = speakers,
        });
        Model(Path.Combine(folder, "model.int8.onnx"));
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "$ 0\n");
        File.WriteAllBytes(Path.Combine(folder, "voices.bin"), new byte[510 * 256 * 4 * 3]);
        return folder;
    }

    public static string Kitten(string folder)
    {
        Model(Path.Combine(folder, "model.int8.onnx"), new Dictionary<string, string>
        {
            ["model_type"] = "kitten-tts", ["sample_rate"] = "24000", ["voice"] = "en-us", ["n_speakers"] = "2",
            ["speaker_names"] = "expr-voice-2-m,expr-voice-2-f",
        });
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "$ 0\n");
        File.WriteAllBytes(Path.Combine(folder, "voices.bin"), new byte[16]);
        return folder;
    }

    public static string Matcha(string folder, bool vocoder = true, string language = "English", string jieba = "0")
    {
        Model(Path.Combine(folder, "model-steps-3.onnx"), new Dictionary<string, string>
        {
            ["model_type"] = "matcha-tts", ["sample_rate"] = "22050", ["voice"] = "en-us", ["has_espeak"] = "1",
            ["jieba"] = jieba, ["language"] = language, ["n_speakers"] = "1",
        });
        Model(Path.Combine(folder, "model-steps-2.onnx"), new Dictionary<string, string> { ["model_type"] = "matcha-tts" });
        File.WriteAllText(Path.Combine(folder, "tokens.txt"), "_ 0\n");
        if (vocoder) Model(Path.Combine(folder, "hifigan_v2.onnx"));
        return folder;
    }

    /// <summary>A .tar.gz or .tar.bz2 of <paramref name="folder"/>, with the folder as its top entry, as sherpa-onnx packs voices.</summary>
    public static string Pack(string folder, string archive)
    {
        using (var file = File.Create(archive))
        using (Stream compressed = archive.EndsWith(".bz2", StringComparison.Ordinal) ? new BZip2OutputStream(file) : new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(compressed, TarEntryFormat.Pax))
        {
            var root = Path.GetFileName(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order())
            {
                var name = root + "/" + Path.GetRelativePath(folder, path).Replace('\\', '/');
                if (Directory.Exists(path)) tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name + "/"));
                else tar.WriteEntry(path, name);
            }
        }
        return archive;
    }

    /// <summary>A .tar.gz with whatever entries <paramref name="write"/> adds, for the refusal tests.</summary>
    public static string Hostile(string archive, Action<TarWriter> write)
    {
        using var file = File.Create(archive);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        write(tar);
        return archive;
    }

    public static PaxTarEntry Entry(string name, string text = "x") =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
}

public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("voice-library-test").FullName;
    private readonly VoiceLibrary _library;

    public VoiceLibraryTests() => _library = new VoiceLibrary(Path.Combine(_dir, "voices"), new AppLog());

    public void Dispose()
    {
        _library.Dispose();
        Directory.Delete(_dir, true);
    }

    private string VoiceFolder(string name) => Path.Combine(_library.Directory, name);

    [Fact]
    public void Onnx_metadata_is_appended_and_read_back_without_touching_the_model()
    {
        var path = Path.Combine(_dir, "m.onnx");
        FakeVoices.Model(path);
        Assert.Empty(OnnxMetadata.Read(path));
        var before = File.ReadAllBytes(path);
        OnnxMetadata.Append(path, new Dictionary<string, string> { ["sample_rate"] = "22050", ["comment"] = "piper ✓" });
        Assert.Equal(new Dictionary<string, string> { ["sample_rate"] = "22050", ["comment"] = "piper ✓" }, OnnxMetadata.Read(path));
        Assert.Equal(before, File.ReadAllBytes(path)[..before.Length]);

        File.WriteAllBytes(path, [0x3A, 0xFF, 0x01, 0x00]); // claims 255 bytes, has 1
        Assert.Throws<InvalidDataException>(() => OnnxMetadata.Read(path));
        File.WriteAllBytes(path, []);
        Assert.Throws<InvalidDataException>(() => OnnxMetadata.Read(path));
    }

    [Fact]
    public void Each_format_is_recognised_from_its_files()
    {
        FakeVoices.Piper(VoiceFolder("en_US-lessac-low"), "en_US-lessac-low", FakeVoices.ClassicConfig);
        FakeVoices.Piper(VoiceFolder("en_US-amy-medium"), "en_US-amy-medium", FakeVoices.ClusterConfig);
        FakeVoices.SherpaVits(VoiceFolder("vits-piper-en_US-glados-high"));
        FakeVoices.Kokoro(VoiceFolder("kokoro-en-v0_19"));
        FakeVoices.Piper(VoiceFolder("zh_CN-xiao_ya-medium"), "zh", """{"phoneme_type":"pinyin","phoneme_id_map":{}}""");
        FakeVoices.Model(Path.Combine(VoiceFolder("lonely"), "lonely.onnx"));
        FakeVoices.Model(Path.Combine(VoiceFolder("bare-sherpa"), "m.onnx"));
        File.WriteAllText(Path.Combine(VoiceFolder("bare-sherpa"), "tokens.txt"), "a 1\n");
        Directory.CreateDirectory(VoiceFolder("empty"));
        Directory.CreateDirectory(VoiceFolder(".import-123"));

        var voices = _library.Voices().ToDictionary(v => v.Id);
        Assert.DoesNotContain(".import-123", voices.Keys);
        Assert.Equal(VoiceFormat.Piper, voices["en_US-lessac-low"].Format);
        Assert.Equal(16000, voices["en_US-lessac-low"].SampleRate);
        var amy = voices["en_US-amy-medium"];
        Assert.Equal(VoiceFormat.PiperClusters, amy.Format);
        Assert.Equal([["a", "ɪ"], ["o", "ʊ"]], amy.VowelClusters);
        var glados = voices["vits-piper-en_US-glados-high"];
        Assert.Equal((VoiceFormat.SherpaVits, 22050, "en-us", "en_US"), (glados.Format, glados.SampleRate, glados.EspeakVoice, glados.LanguageCode));
        var kokoro = voices["kokoro-en-v0_19"];
        Assert.Equal(VoiceFormat.Kokoro, kokoro.Format);
        Assert.Equal("model.onnx", Path.GetFileName(kokoro.ModelPath)); // full precision before int8
        Assert.Equal(["af", "af_bella", "am_adam"], kokoro.Speakers.Select(s => s.Name));
        Assert.Contains("pinyin", voices["zh_CN-xiao_ya-medium"].Problem);
        Assert.Contains("lonely.onnx.json", voices["lonely"].Problem);
        Assert.Contains("lacks the settings", voices["bare-sherpa"].Problem);
        Assert.Contains("no voice model", voices["empty"].Problem);
        Assert.All(voices.Values.Where(v => v.Problem is not null), v => Assert.Equal(VoiceFormat.Unknown, v.Format));
    }

    [Fact]
    public void Piper_voices_get_tokens_and_metadata_for_sherpa_onnx_with_clusters_as_private_characters()
    {
        FakeVoices.Piper(VoiceFolder("amy"), "amy", FakeVoices.ClusterConfig);
        var amy = _library.Find("amy")!;
        var (tokens, clusters) = _library.PrepareForSherpa(amy);
        var lines = File.ReadAllLines(tokens);
        Assert.Contains("a 14", lines);
        Assert.Contains("\uE000 161", lines);
        Assert.Contains("\uE001 165", lines);
        Assert.Equal(2, clusters.Count);
        Assert.Equal(["a", "ɪ"], clusters[0].Phonemes);
        Assert.Equal("\uE000", clusters[0].Symbol);
        Assert.Equal(["o", "ʊ"], clusters[1].Phonemes);
        Assert.Equal("\uE001", clusters[1].Symbol);
        var meta = OnnxMetadata.Read(amy.ModelPath!);
        Assert.Equal(("piper", "22050", "en-us", "vits"), (meta["comment"], meta["sample_rate"], meta["voice"], meta["model_type"]));

        var size = new FileInfo(amy.ModelPath!).Length;
        var written = File.GetLastWriteTimeUtc(tokens);
        Assert.Same(_library.PrepareForSherpa(amy), _library.PrepareForSherpa(amy)); // kept, not redone for every line
        Assert.Equal(written, File.GetLastWriteTimeUtc(tokens));
        _library.Refresh();
        _library.PrepareForSherpa(amy);
        Assert.Equal(size, new FileInfo(amy.ModelPath!).Length); // metadata only added once
        Assert.Empty(Directory.GetFiles(Path.Combine(amy.Folder, ".cuebot"), "*.tmp"));

        var request = VoiceLibraryEngine.EngineRequest(amy, amy.Speaker(null), "Hi", 0, _library);
        Assert.Equal("vits", (string?)request["kind"]);
        Assert.Equal(tokens, (string?)request["tokens"]);
        Assert.Equal("""[[["a","\u026A"],"\uE000"],[["o","\u028A"],"\uE001"]]""", request["clusters"]!.ToJsonString());
        Assert.Equal(1.0, (double)request["speed"]!);
    }

    [Fact]
    public void Kokoro_and_sherpa_voices_are_sent_as_they_are()
    {
        FakeVoices.Kokoro(VoiceFolder("kokoro"));
        var kokoro = _library.Find("kokoro")!;
        var request = VoiceLibraryEngine.EngineRequest(kokoro, kokoro.Speaker("am_adam"), "Hi", 10, _library);
        Assert.Equal(("kokoro", kokoro.TokensPath, kokoro.VoicesPath, 2), ((string?)request["kind"], (string?)request["tokens"], (string?)request["voices"], (int)request["sid"]!));
        Assert.Equal(2.0, (double)request["speed"]!);
        Assert.Null(request["clusters"]);
    }

    [Fact]
    public void Kitten_and_Matcha_are_recognised_by_their_model_type()
    {
        FakeVoices.Kitten(VoiceFolder("kitten-nano-en-v0_8-int8"));
        FakeVoices.Matcha(VoiceFolder("matcha-icefall-en_US-ljspeech"));
        FakeVoices.Matcha(VoiceFolder("no-vocoder"), vocoder: false);
        FakeVoices.Matcha(VoiceFolder("matcha-zh"), language: "Chinese", jieba: "1");
        var voices = _library.Voices().ToDictionary(v => v.Id);

        var kitten = voices["kitten-nano-en-v0_8-int8"];
        Assert.Equal((VoiceFormat.Kitten, 24000), (kitten.Format, kitten.SampleRate)); // voices.bin as Kokoro, but not Kokoro
        Assert.Equal(["expr-voice-2-m", "expr-voice-2-f"], kitten.Speakers.Select(s => s.Name));
        var request = VoiceLibraryEngine.EngineRequest(kitten, kitten.Speaker("expr-voice-2-f"), "Hi", 0, _library);
        Assert.Equal(("kitten", 1, kitten.VoicesPath), ((string?)request["kind"], (int)request["sid"]!, (string?)request["voices"]));

        var matcha = voices["matcha-icefall-en_US-ljspeech"];
        Assert.Equal(VoiceFormat.Matcha, matcha.Format);
        Assert.Equal("model-steps-3.onnx", Path.GetFileName(matcha.ModelPath));
        Assert.Equal("hifigan_v2.onnx", Path.GetFileName(matcha.VocoderPath));
        request = VoiceLibraryEngine.EngineRequest(matcha, null, "Hi", 0, _library);
        Assert.Equal(("matcha", matcha.VocoderPath), ((string?)request["kind"], (string?)request["vocoder"]));
        Assert.Contains("needs a vocoder", voices["no-vocoder"].Problem);
        Assert.Contains("word list", voices["matcha-zh"].Problem);
    }

    [Fact]
    public void Kokoro_speakers_get_the_espeak_voice_of_their_language_and_unsupported_ones_are_left_out()
    {
        FakeVoices.Kokoro(VoiceFolder("kokoro-multi-lang-v1_0"), "af_heart,bf_emma,ef_dora,ff_siwis,hm_omega,if_sara,pf_dora,jf_alpha,zf_xiaobei");
        var kokoro = _library.Find("kokoro-multi-lang-v1_0")!;
        Assert.Equal(VoiceFormat.Kokoro, kokoro.Format);
        Assert.Equal(
            [("af_heart", 0, "en-us"), ("bf_emma", 1, "en-gb-x-rp"), ("ef_dora", 2, "es"), ("ff_siwis", 3, "fr"), ("hm_omega", 4, "hi"), ("if_sara", 5, "it"), ("pf_dora", 6, "pt-br")],
            kokoro.Speakers.Select(s => (s.Name, s.Id, s.EspeakVoice)));
        Assert.Contains("2 Japanese and Mandarin speakers are left out", kokoro.Note);
        var request = VoiceLibraryEngine.EngineRequest(kokoro, kokoro.Speaker("ef_dora"), "Hola", 0, _library);
        Assert.Equal(("es", 2), ((string?)request["espeak_voice"], (int)request["sid"]!));

        FakeVoices.Kokoro(VoiceFolder("kokoro-zh"), "zf_xiaobei,zm_yunjian");
        _library.Refresh();
        Assert.Contains("Japanese and Mandarin", _library.Find("kokoro-zh")!.Problem);
    }

    [Fact]
    public void A_damaged_model_is_reported_on_the_voice_not_on_every_line()
    {
        FakeVoices.Piper(VoiceFolder("cut"), "cut", FakeVoices.ClassicConfig);
        using (var file = File.OpenWrite(Path.Combine(VoiceFolder("cut"), "cut.onnx"))) file.SetLength(3); // graph header claims 300 bytes
        var voice = _library.Find("cut")!;
        Assert.Equal(VoiceFormat.Unknown, voice.Format);
        Assert.Contains("damaged or incomplete", voice.Problem);
    }

    [Theory]
    [InlineData(VoiceFormat.Piper, false, false, VoiceBackend.None)]
    [InlineData(VoiceFormat.Piper, false, true, VoiceBackend.PiperProgram)]
    [InlineData(VoiceFormat.Piper, true, true, VoiceBackend.VoiceEngine)]
    [InlineData(VoiceFormat.PiperClusters, false, true, VoiceBackend.None)]
    [InlineData(VoiceFormat.PiperClusters, true, false, VoiceBackend.VoiceEngine)]
    [InlineData(VoiceFormat.SherpaVits, true, true, VoiceBackend.VoiceEngine)]
    [InlineData(VoiceFormat.Kokoro, false, true, VoiceBackend.None)]
    [InlineData(VoiceFormat.Kokoro, true, false, VoiceBackend.VoiceEngine)]
    public void The_best_program_that_can_speak_a_voice_is_chosen(VoiceFormat format, bool engine, bool piperProgram, VoiceBackend expected)
    {
        var voice = new LibraryVoice("v", _dir, format, "en", 22050, [], null);
        var (backend, why) = VoiceLibraryEngine.ChooseBackend(voice, engine, piperProgram);
        Assert.Equal(expected, backend);
        if (backend == VoiceBackend.None) Assert.False(string.IsNullOrEmpty(why));
        var broken = voice with { Format = VoiceFormat.Unknown, Problem = "broken" };
        Assert.Equal((VoiceBackend.None, "broken"), VoiceLibraryEngine.ChooseBackend(broken, true, true));
    }

    [Fact]
    public async Task A_voice_nothing_can_speak_says_why_instead_of_crashing()
    {
        FakeVoices.Piper(VoiceFolder("en_US-amy-medium"), "en_US-amy-medium", FakeVoices.ClusterConfig);
        var store = new PiperStore(Path.Combine(_dir, "piper"), new HttpClient(), new AppLog(), _library);
        var engine = new VoiceLibraryEngine(_library, new VoiceEngine(Path.Combine(_dir, "voice-engine"), new HttpClient(), new AppLog()),
            store, () => new PiperSettings());
        Assert.False(engine.IsAvailable);
        Assert.Empty(await engine.GetVoicesAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.SynthesizeAsync(new SynthesisRequest("Hi", "en_US-amy-medium"), CancellationToken.None));
        Assert.Contains(VoiceEngine.CanInstall ? "Get voice engine" : "no build", ex.Message);
        Assert.Equal(ex.Message, engine.UnavailableReason);
    }

    [Fact]
    public void Piper_voices_of_older_Cuebot_versions_move_into_the_library()
    {
        var piper = Path.Combine(_dir, "piper");
        FakeVoices.Piper(Path.Combine(piper, "voices"), "en_US-lessac-low", FakeVoices.ClassicConfig);
        FakeVoices.Model(Path.Combine(piper, "voices", "half.onnx")); // no .json: left alone
        Directory.CreateDirectory(Path.Combine(piper, "compat"));
        File.WriteAllText(Path.Combine(piper, "compat", "x.onnx.json"), "{}");
        Directory.CreateDirectory(Path.Combine(piper, "program"));

        Assert.Equal(1, _library.MigrateLegacyPiperVoices(piper));
        Assert.True(File.Exists(Path.Combine(VoiceFolder("en_US-lessac-low"), "en_US-lessac-low.onnx")));
        Assert.True(File.Exists(Path.Combine(VoiceFolder("en_US-lessac-low"), "en_US-lessac-low.onnx.json")));
        Assert.False(File.Exists(Path.Combine(piper, "voices", "en_US-lessac-low.onnx")));
        Assert.True(File.Exists(Path.Combine(piper, "voices", "half.onnx")));
        Assert.False(Directory.Exists(Path.Combine(piper, "compat")));
        Assert.True(Directory.Exists(Path.Combine(piper, "program")));
        Assert.Equal(VoiceFormat.Piper, _library.Find("en_US-lessac-low")!.Format);
        Assert.Equal(0, _library.MigrateLegacyPiperVoices(piper)); // once only
        Assert.True(File.Exists(Path.Combine(VoiceFolder("en_US-lessac-low"), VoiceLibrary.DownloadedMarker))); // left out of backups
    }

    [Fact]
    public void A_voice_whose_second_file_cannot_move_stays_where_it_was_and_moves_next_time()
    {
        var piper = Path.Combine(_dir, "piper");
        FakeVoices.Piper(Path.Combine(piper, "voices"), "en_US-lessac-low", FakeVoices.ClassicConfig);
        var original = VoiceLibrary.MoveFile;
        try
        {
            VoiceLibrary.MoveFile = (from, to) =>
            {
                if (from.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("in use");
                File.Move(from, to);
            };
            Assert.Equal(0, _library.MigrateLegacyPiperVoices(piper));
        }
        finally
        {
            VoiceLibrary.MoveFile = original;
        }
        Assert.True(File.Exists(Path.Combine(piper, "voices", "en_US-lessac-low.onnx"))); // moved back
        Assert.False(Directory.Exists(VoiceFolder("en_US-lessac-low")));
        Assert.Empty(Directory.GetDirectories(_library.Directory, ".migrate-*"));

        Assert.Equal(1, _library.MigrateLegacyPiperVoices(piper));
        Assert.Equal(VoiceFormat.Piper, _library.Find("en_US-lessac-low")!.Format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Piper_voice_files_are_imported_by_copy_or_move(bool move)
    {
        var source = FakeVoices.Piper(Path.Combine(_dir, "downloads"), "my-voice", FakeVoices.ClassicConfig);
        var ids = await _library.ImportAsync([Path.Combine(source, "my-voice.onnx")], move);   // the .json is found next to it
        Assert.Equal(["my-voice"], ids);
        Assert.Equal(VoiceFormat.Piper, _library.Find("my-voice")!.Format);
        Assert.Equal(!move, File.Exists(Path.Combine(source, "my-voice.onnx")));

        if (!move)
        {
            Assert.Equal(["my-voice (2)"], await _library.ImportAsync([Path.Combine(source, "my-voice.onnx.json")], false));
        }
        Assert.Empty(Directory.GetDirectories(_library.Directory, ".import-*"));
    }

    [Fact]
    public async Task A_refused_or_cancelled_move_import_keeps_the_picked_files()
    {
        var source = FakeVoices.Piper(Path.Combine(_dir, "downloads"), "zh", """{"phoneme_type":"pinyin","phoneme_id_map":{}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.ImportAsync([Path.Combine(source, "zh.onnx")], move: true));
        Assert.True(File.Exists(Path.Combine(source, "zh.onnx")));
        Assert.True(File.Exists(Path.Combine(source, "zh.onnx.json")));

        var good = FakeVoices.Piper(Path.Combine(_dir, "downloads2"), "good", FakeVoices.ClassicConfig);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _library.ImportAsync([Path.Combine(good, "good.onnx")], move: true, ct: cancelled.Token));
        Assert.True(File.Exists(Path.Combine(good, "good.onnx")));
        Assert.Empty(Directory.GetFileSystemEntries(_library.Directory));
    }

    [Theory]
    [InlineData(".tar.bz2")]
    [InlineData(".tar.gz")]
    public async Task Voice_packages_are_unpacked_into_a_folder_of_their_own(string extension)
    {
        var glados = FakeVoices.Pack(FakeVoices.SherpaVits(Path.Combine(_dir, "src", "vits-piper-en_US-glados-high")),
            Path.Combine(_dir, "vits-piper-en_US-glados-high" + extension));
        var kokoro = FakeVoices.Pack(FakeVoices.Kokoro(Path.Combine(_dir, "src", "kokoro-int8-en-v0_19")), Path.Combine(_dir, "kokoro" + extension));

        var ids = await _library.ImportAsync([glados, kokoro], move: true);
        Assert.Equal(["vits-piper-en_US-glados-high", "kokoro-int8-en-v0_19"], ids);
        Assert.Equal(VoiceFormat.SherpaVits, _library.Find(ids[0])!.Format);
        Assert.Equal(VoiceFormat.Kokoro, _library.Find(ids[1])!.Format);
        Assert.True(File.Exists(Path.Combine(VoiceFolder(ids[0]), "espeak-ng-data", "phontab")));
        Assert.False(File.Exists(glados)); // moved
        Assert.Empty(Directory.GetDirectories(_library.Directory, ".import-*"));
    }

    public static TheoryData<string, Action<TarWriter>> HostilePackages => new()
    {
        { "leaves its folder", t => t.WriteEntry(FakeVoices.Entry("voice/../../evil.txt")) },
        { "absolute path", t => t.WriteEntry(FakeVoices.Entry("/tmp/evil.txt")) },
        { "absolute path", t => t.WriteEntry(FakeVoices.Entry("C:/evil.txt")) },
        { "link", t => t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "voice/link") { LinkName = "/etc/passwd" }) },
        { "link", t => t.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "voice/hard") { LinkName = "voice/a" }) },
        { "not a file or folder", t => t.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "voice/pipe")) },
        { "no voice model", t => t.WriteEntry(FakeVoices.Entry("voice/readme.txt")) },
    };

    [Theory]
    [MemberData(nameof(HostilePackages))]
    public async Task Packages_that_could_harm_or_hold_no_voice_are_refused(string why, Action<TarWriter> write)
    {
        var archive = FakeVoices.Hostile(Path.Combine(_dir, "bad.tar.gz"), write);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _library.ImportAsync([archive], move: true));
        Assert.Contains(why, ex.Message);
        Assert.True(File.Exists(archive)); // a refused package is never deleted
        Assert.Empty(Directory.GetFileSystemEntries(_library.Directory));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public async Task Packages_too_big_or_with_too_many_files_are_refused()
    {
        var archive = FakeVoices.Hostile(Path.Combine(_dir, "big.tar.gz"), t =>
        {
            for (var i = 0; i < 5; i++) t.WriteEntry(FakeVoices.Entry($"v/{i}.txt", new string('x', 100)));
        });
        var many = await Assert.ThrowsAsync<InvalidDataException>(() => VoiceArchive.ExtractAsync(archive, Path.Combine(_dir, "a"), default, maxFiles: 4));
        Assert.Contains("more than 4 files", many.Message);
        var big = await Assert.ThrowsAsync<InvalidDataException>(() => VoiceArchive.ExtractAsync(archive, Path.Combine(_dir, "b"), default, maxBytes: 450));
        Assert.Contains("unpacks to more than", big.Message);
        await VoiceArchive.ExtractAsync(archive, Path.Combine(_dir, "c"), default, maxFiles: 5, maxBytes: 500);
        Assert.Equal(5, Directory.GetFiles(Path.Combine(_dir, "c", "v")).Length);
    }

    [Fact]
    public async Task Other_files_are_refused_with_what_is_accepted()
    {
        var path = Path.Combine(_dir, "voice.zip");
        File.WriteAllText(path, "x");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _library.ImportAsync([path], false));
        Assert.Contains(".tar.bz2", ex.Message);
    }

    [Fact]
    public async Task Changes_made_in_the_folder_by_hand_are_noticed()
    {
        var changed = new TaskCompletionSource();
        _library.Changed += () => changed.TrySetResult();
        _library.Watch();
        Assert.Empty(_library.Voices());
        FakeVoices.SherpaVits(Path.Combine(_dir, "outside", "glados"));
        Directory.Move(Path.Combine(_dir, "outside", "glados"), VoiceFolder("glados"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VoiceFormat.SherpaVits, Assert.Single(_library.Voices()).Format);
    }

    [Fact]
    public void Voice_folder_names_cannot_leave_the_library()
    {
        Assert.Equal("voice", VoiceLibrary.SafeName(".."));
        Assert.Equal("a_b", VoiceLibrary.SafeName("a/b"));
        Assert.Equal("evil", VoiceLibrary.SafeName("../evil").TrimStart('_'));
        Assert.Throws<InvalidDataException>(() => VoiceArchive.Target(_dir, "a/../../b"));
        Assert.Null(VoiceArchive.Target(_dir, "./"));
        Assert.Equal(Path.Combine(_dir, "a", "b"), VoiceArchive.Target(_dir, "./a/b"));
    }
}

public sealed class PiperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("piper-test").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Catalog = """
        {
          "en_US-lessac-medium": {"key":"en_US-lessac-medium","name":"lessac","language":{"code":"en_US","name_english":"English","country_english":"United States"},
            "quality":"medium","num_speakers":1,"speaker_id_map":{},
            "files":{"en/en_US/lessac/medium/en_US-lessac-medium.onnx":{"size_bytes":10,"md5_digest":"aa"},
                     "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json":{"size_bytes":5,"md5_digest":"bb"},
                     "en/en_US/lessac/medium/MODEL_CARD":{"size_bytes":1,"md5_digest":"cc"}}},
          "de_DE-thorsten-high": {"name":"thorsten","language":{"code":"de_DE","name_english":"German","country_english":"Germany"},
            "quality":"high","num_speakers":2,
            "files":{"de/x.onnx":{"size_bytes":1,"md5_digest":"d"},"de/x.onnx.json":{"size_bytes":1,"md5_digest":"e"}}},
          "zh_CN-xiao_ya-medium": {"name":"xiao_ya","language":{"code":"zh_CN","name_english":"Chinese","country_english":"China"},
            "quality":"medium","num_speakers":1,
            "files":{"zh/m.onnx":{"size_bytes":1,"md5_digest":"f"},"zh/m.onnx.json":{"size_bytes":1,"md5_digest":"g"}}},
          "broken": {"name":"no files"}
        }
        """;

    [Fact]
    public void Catalog_is_parsed_and_sorted_by_what_the_picker_shows()
    {
        var voices = PiperStore.ParseCatalog(Catalog);
        Assert.Equal(["English (United States): Lessac, medium", "German (Germany): Thorsten, high, 2 voices"], voices.Select(v => v.Display));
        Assert.Equal("en/en_US/lessac/medium/en_US-lessac-medium.onnx", voices[0].Model.Path);
        Assert.Equal(15, voices[0].SizeBytes);
        Assert.Contains("zh_CN-xiao_ya-medium", (IReadOnlySet<string>)PiperStore.UnsupportedVoices); // so it is hidden above
        Assert.Equal("https://huggingface.co/rhasspy/piper-voices/resolve/main/", PiperStore.VoicesBaseUrl);
    }

    [Fact]
    public void The_Piper_program_gets_its_arguments_and_a_compatible_config()
    {
        var library = new VoiceLibrary(Path.Combine(_dir, "voices"), new AppLog());
        FakeVoices.Piper(Path.Combine(library.Directory, "en_GB-vctk-medium"), "en_GB-vctk-medium",
            """{"audio":{"sample_rate":16000},"language":{"code":"en_GB"},"speaker_id_map":{"p239":1,"p225":0},"phoneme_id_map":{"a":[1]}}""");
        FakeVoices.Piper(Path.Combine(library.Directory, "it_IT-serena-medium"), "it_IT-serena-medium", FakeVoices.ListsClustersConfig);
        var vctk = library.Find("en_GB-vctk-medium")!;
        Assert.Equal(["p225", "p239"], vctk.Speakers.Select(s => s.Name));
        Assert.Equal(["--model", vctk.ModelPath!, "--output_raw", "--length_scale", "1.6", "--speaker", "1"],
            VoiceLibraryEngine.BuildArguments(vctk, "p239", rate: -10));
        Assert.Equal(["--model", vctk.ModelPath!, "--output_raw", "--length_scale", "1", "--config", "c.json"],
            VoiceLibraryEngine.BuildArguments(vctk, null, 0, "c.json"));
        Assert.Equal(vctk.ConfigPath, PiperStore.PrepareConfig(vctk));
        Assert.DoesNotContain("--config", VoiceLibraryEngine.BuildArguments(vctk, null, 0, PiperStore.PrepareConfig(vctk)));
        Assert.Equal(0.5, VoiceLibraryEngine.LengthScale(10));
        Assert.Equal("one two three", VoiceLibraryEngine.OneLine(" one\r\ntwo\n\nthree "));
        Assert.Equal(("a", "b"), VoiceLibraryEngine.SplitVoiceId("a#b"));

        var serena = library.Find("it_IT-serena-medium")!;
        Assert.Equal(VoiceFormat.Piper, serena.Format); // lists clusters but does not use them
        var compat = PiperStore.PrepareConfig(serena);
        Assert.Equal(Path.Combine(serena.Folder, ".cuebot", "compat.onnx.json"), compat);
        Assert.DoesNotContain("aɪ", JsonNode.Parse(File.ReadAllText(compat))!["phoneme_id_map"]!.AsObject().Select(p => p.Key));
        Assert.Equal(compat, PiperStore.PrepareConfig(serena));
        Assert.Single(library.Voices(), v => v.Id == "it_IT-serena-medium"); // the copy is not a voice of its own
    }

    [Fact]
    public void Configs_lose_the_phonemes_Piper_2023_cannot_read()
    {
        var cleaned = JsonNode.Parse(PiperStore.CompatibleConfig(FakeVoices.ListsClustersConfig)!)!;
        Assert.Equal(["_", "a", "ɪ", "ˈ"], cleaned["phoneme_id_map"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["ɐ", "ʧ"], cleaned["phoneme_map"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["."], cleaned["inference"]!["phoneme_silence"]!.AsObject().Select(p => p.Key));
        Assert.Equal(0.5, cleaned["inference"]!["noise_scale"]!.GetValue<double>());
        Assert.Null(PiperStore.CompatibleConfig("""{"phoneme_id_map":{"_":[0],"ˈ":[120],"ɪ":[74]},"phoneme_map":{"ɐ":["a"]}}"""));
    }

    [Fact]
    public void Only_the_downloaded_program_counts_as_Piper_2023()
    {
        var store = new PiperStore(_dir, new HttpClient(), new AppLog(), new VoiceLibrary(Path.Combine(_dir, "voices"), new AppLog()));
        var downloaded = Path.Combine(store.ProgramDirectory, "piper", OperatingSystem.IsWindows() ? "piper.exe" : "piper");
        Assert.True(store.IsDownloadedProgram(downloaded));
        Assert.False(store.IsDownloadedProgram(Path.Combine(_dir, "my-piper", "piper")));
    }

    [Fact]
    public void Piper_program_failures_say_what_to_do_instead_of_a_bare_exit_code()
    {
        var crash = VoiceLibraryEngine.DescribeFailure(-1073740791, "", "en_US-amy-medium", windows: true);
        Assert.Contains("Piper crashed while speaking with the voice \"en_US-amy-medium\" (error 0xC0000409)", crash);
        Assert.Contains("Get voice engine", crash);
        Assert.DoesNotContain("-1073740791", crash);

        var linux = VoiceLibraryEngine.DescribeFailure(134,
            "[2026-11-24 03:41:43.886] [piper] [error] \"aɪ\" is not a single codepoint (ids=161,)\n"
            + "terminate called after throwing an instance of 'std::runtime_error'\n  what():  Phonemes must be one codepoint\n",
            "en_US-amy-medium", windows: false);
        Assert.Contains("Piper crashed", linux);
        Assert.EndsWith("Piper said: \"aɪ\" is not a single codepoint (ids=161,) what():  Phonemes must be one codepoint", linux);

        Assert.Contains("missing or damaged", VoiceLibraryEngine.DescribeFailure(unchecked((int)0xC0000135), "", "v", windows: true));
        Assert.Contains("error 2 ", VoiceLibraryEngine.DescribeFailure(2, "[x] [piper] [info] Loaded voice", "v", windows: false));
        Assert.DoesNotContain("Piper said", VoiceLibraryEngine.DescribeFailure(2, "[x] [piper] [info] Loaded voice", "v", windows: false));
    }

    [Fact]
    public void Every_desktop_platform_but_the_Mac_has_a_pinned_Piper_program()
    {
        var release = PiperRelease.ForThisComputer();
        if (OperatingSystem.IsMacOS())
        {
            Assert.Null(release);   // the release's macOS archives do not run; the voice engine speaks there
            return;
        }
        Assert.NotNull(release);
        Assert.Equal(64, release!.Sha256.Length);
        Assert.StartsWith("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/", release.Url.ToString());
    }

    [Fact]
    public void Every_desktop_platform_has_pinned_voice_engine_packages()
    {
        Assert.Equal(6, VoiceEnginePins.Targets.Count());
        foreach (var target in VoiceEnginePins.Targets)
        {
            var wheels = VoiceEnginePins.For(target)!;
            Assert.Equal(3, wheels.Count);
            Assert.All(wheels, w => Assert.Matches("^[0-9a-f]{64}$", w.Sha256));
            Assert.All(wheels, w => Assert.StartsWith("https://", w.Url));
            Assert.Single(wheels, w => w.FileName.StartsWith("sherpa_onnx_core-" + VoiceEnginePins.SherpaVersion, StringComparison.Ordinal));
            Assert.Single(wheels, w => w.FileName.StartsWith("piper_phonemize-" + VoiceEnginePins.PhonemizeVersion, StringComparison.Ordinal));
        }
        Assert.True(VoiceEngine.CanInstall || RuntimeInformationIsUnusual());
    }

    private static bool RuntimeInformationIsUnusual() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture is not (System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64);
}
