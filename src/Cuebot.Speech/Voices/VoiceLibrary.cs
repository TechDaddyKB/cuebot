using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>What kind of voice a library folder holds, worked out from its files.</summary>
public enum VoiceFormat
{
    /// <summary>No voice Cuebot can use (see <see cref="LibraryVoice.Problem"/>).</summary>
    Unknown,

    /// <summary>A Piper voice (<c>.onnx</c> and <c>.onnx.json</c>) that Piper 2023.11.14-2 can speak too.</summary>
    Piper,

    /// <summary>A Piper voice made with Piper 1.5 or later that merges vowels such as "aɪ" ("vowel_clusters").</summary>
    PiperClusters,

    /// <summary>A sherpa-onnx VITS package (model with sherpa-onnx metadata and <c>tokens.txt</c>), such as vits-piper-*.</summary>
    SherpaVits,

    /// <summary>A sherpa-onnx Kokoro package (model, <c>voices.bin</c> and <c>tokens.txt</c>).</summary>
    Kokoro,

    /// <summary>A sherpa-onnx Kitten package (model, <c>voices.bin</c> and <c>tokens.txt</c>; model_type "kitten-tts").</summary>
    Kitten,

    /// <summary>A sherpa-onnx Matcha package (acoustic model and <c>tokens.txt</c>) with its vocoder (<see cref="VoiceLibrary.Vocoder"/>).</summary>
    Matcha,
}

/// <summary>One speaker of a voice: its name, the id the model knows it by, and the espeak-ng voice for its language.</summary>
public sealed record VoiceSpeaker(string Name, int Id, string EspeakVoice);

/// <summary>A vocoder file Cuebot fetches for Matcha voices, pinned like the voice engine's packages.</summary>
public sealed record VocoderFile(string FileName, string Url, string Sha256, long Size);

/// <summary>One folder of the voice library and what Cuebot found in it.</summary>
public sealed record LibraryVoice(string Id, string Folder, VoiceFormat Format, string LanguageCode, int SampleRate,
    IReadOnlyList<VoiceSpeaker> Speakers, string? Problem)
{
    public string? ModelPath { get; init; }

    /// <summary>Piper's <c>.onnx.json</c>.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>sherpa-onnx's <c>tokens.txt</c> (for Piper voices, Cuebot writes one, see <see cref="VoiceLibrary.PrepareForSherpa"/>).</summary>
    public string? TokensPath { get; init; }

    /// <summary>Kokoro's or Kitten's <c>voices.bin</c>.</summary>
    public string? VoicesPath { get; init; }

    /// <summary>Matcha's vocoder.</summary>
    public string? VocoderPath { get; init; }

    /// <summary>The espeak-ng voice that turns text into phonemes, e.g. "en-us" (a speaker may have its own).</summary>
    public string EspeakVoice { get; init; } = "";

    /// <summary>Piper's phoneme_type: "espeak" or "text".</summary>
    public string PhonemeType { get; init; } = "espeak";

    public IReadOnlyList<IReadOnlyList<string>> VowelClusters { get; init; } = [];

    /// <summary>Something worth knowing that does not stop the voice, e.g. speakers left out.</summary>
    public string? Note { get; init; }

    public string FormatName => Format switch
    {
        VoiceFormat.Piper => "Piper voice",
        VoiceFormat.PiperClusters => "Piper voice (Piper 1.5 or later)",
        VoiceFormat.SherpaVits => "sherpa-onnx voice",
        VoiceFormat.Kokoro => "Kokoro voice",
        VoiceFormat.Kitten => "Kitten voice",
        VoiceFormat.Matcha => "Matcha voice",
        _ => "Not a voice",
    };

    /// <summary>The speaker a voice id's "#name" part picks, or the first one.</summary>
    public VoiceSpeaker? Speaker(string? name) =>
        Speakers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Speakers.FirstOrDefault();
}

/// <summary>
/// The shared voice library (<c>voices</c> in the data folder): one folder per voice, holding the voice's files as its
/// maker published them. Voices arrive by download from Piper's catalog (<see cref="PiperStore"/>), by import
/// (<see cref="ImportAsync"/>), or by copying a folder in by hand; Cuebot notices changes to the folder by itself.
/// Folders whose name starts with "." are Cuebot's own (imports in progress) and are not voices. Inside a voice folder,
/// <c>.cuebot</c> holds what Cuebot adds (a tokens.txt for sherpa-onnx, a config copy for Piper 2023, and
/// <see cref="DownloadedMarker"/> for voices Cuebot can download again, which backups leave out).
/// </summary>
public sealed class VoiceLibrary : IDisposable
{
    private const string LogSource = "Speech";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Marks a voice that came from Piper's catalog: backups leave it out, since Cuebot can download it again.</summary>
    public static readonly string DownloadedMarker = Path.Combine(".cuebot", "downloaded");

    /// <summary>
    /// The vocoder Cuebot adds to Matcha voices that come without one (sherpa-onnx publishes them separately). HiFi-GAN
    /// v2 (22.05 kHz): in CI's Whisper checks it spoke the English Matcha voice as accurately as vocos-22khz-univ, at a
    /// fourteenth of the size, and sherpa-onnx publishes its checksum.
    /// </summary>
    public static readonly VocoderFile Vocoder = new("hifigan_v2.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/vocoder-models/hifigan_v2.onnx",
        "a41d404cce7924493540238da5b30a4bc14b6ddaf1a37f3c79fa4f59548c19f0", 3749714);

    private readonly AppLog _log;
    private readonly HttpClient? _http;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, (DateTime Stamp, SherpaPreparation Result)> _prepared = new(StringComparer.Ordinal);
    private IReadOnlyList<LibraryVoice>? _cache;
    private DateTime _cachedAt;
    private long _generation;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    /// <param name="http">Fetches Matcha's vocoder on import; without it such voices need theirs copied in.</param>
    public VoiceLibrary(string directory, AppLog log, HttpClient? http = null)
    {
        Directory = directory;
        _log = log;
        _http = http;
    }

    public string Directory { get; }

    /// <summary>Raised (on a background thread) when voices were added, changed or removed, by Cuebot or by hand.</summary>
    public event Action? Changed;

    /// <summary>Starts noticing changes made to the folder outside Cuebot (a second later, once copying settles).</summary>
    public void Watch()
    {
        if (_watcher is not null) return;
        System.IO.Directory.CreateDirectory(Directory);
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
        FileSystemEventHandler onChange = (_, e) =>
        {
            var relative = Path.GetRelativePath(Directory, e.FullPath);
            if (relative.StartsWith('.') || relative.Contains($"{Path.DirectorySeparatorChar}.cuebot", StringComparison.Ordinal)) return;
            _debounce?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        };
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Changed += onChange;
        _watcher.Renamed += (s, e) => onChange(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Forgets what was found and tells listeners to look again.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _cache = null;
            _generation++;
        }
        _prepared.Clear();
        Changed?.Invoke();
    }

    /// <summary>Every voice folder, sorted by name, including ones with a <see cref="LibraryVoice.Problem"/>.</summary>
    public IReadOnlyList<LibraryVoice> Voices()
    {
        long generation;
        lock (_lock)
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAt < CacheLifetime) return _cache;
            generation = _generation;
        }
        var list = new List<LibraryVoice>();
        if (System.IO.Directory.Exists(Directory))
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(folder).StartsWith('.')) continue;
                try
                {
                    list.Add(Inspect(folder));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    list.Add(new LibraryVoice(Path.GetFileName(folder), folder, VoiceFormat.Unknown, "", 0, [], $"Cuebot could not read this folder: {ex.Message}"));
                }
            }
        lock (_lock)
        {
            // A Refresh() while this scan ran may mean the folder changed under it: answer, but do not keep the answer.
            if (generation == _generation)
            {
                _cache = list;
                _cachedAt = DateTime.UtcNow;
            }
        }
        return list;
    }

    public LibraryVoice? Find(string id) => Voices().FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string id) => System.IO.Directory.Exists(Path.Combine(Directory, SafeName(id)));

    public void Delete(string id)
    {
        var folder = Path.Combine(Directory, SafeName(id));
        if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
        Refresh();
    }

    /// <summary>Marks a voice folder as one Cuebot can download again (see <see cref="DownloadedMarker"/>).</summary>
    internal static void MarkDownloaded(string folder)
    {
        var marker = Path.Combine(folder, DownloadedMarker);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(marker) ?? folder);
        File.WriteAllText(marker, "Downloaded from Piper's voice catalog; left out of backups.\n");
    }

    // ---------------------------------------------------------------- what a folder holds

    /// <summary>Works out what kind of voice <paramref name="folder"/> holds. Never throws for odd content: it reports a problem.</summary>
    public static LibraryVoice Inspect(string folder)
    {
        var id = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        LibraryVoice Problem(string why) => new(id, folder, VoiceFormat.Unknown, "", 0, [], why);
        var files = System.IO.Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList();
        var onnx = files.Where(f => f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var vocoders = onnx.Where(IsVocoder).ToList();
        var models = onnx.Except(vocoders).ToList();
        string? Named(string name) => files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        if (models.Count == 0)
            return Problem("There is no voice model (.onnx file) in this folder.");

        // A model cut short (an interrupted copy or download) is reported here, not as a failure on every line.
        foreach (var model in models)
        {
            try
            {
                OnnxMetadata.Read(model);
            }
            catch (InvalidDataException ex)
            {
                return Problem($"{Path.GetFileName(model)} is damaged or incomplete ({ex.Message}). Delete this voice and download or import it again.");
            }
        }

        var tokens = Named("tokens.txt");
        var voicesBin = Named("voices.bin");
        try
        {
            if (models.FirstOrDefault(m => File.Exists(m + ".json")) is { } piperModel)
                return InspectPiper(id, folder, piperModel, piperModel + ".json");
            if (tokens is null)
            {
                var name = Path.GetFileName(models[0]);
                return Problem($"Cuebot found {name} but not what goes with it: a Piper voice also needs {name}.json, and a sherpa-onnx voice needs tokens.txt.");
            }
            var preferred = PreferredModel(models);
            var meta = OnnxMetadata.Read(preferred);
            var type = meta.GetValueOrDefault("model_type", "");
            return type switch
            {
                "kokoro" when voicesBin is not null => InspectKokoro(id, folder, preferred, tokens, voicesBin, meta),
                "kitten-tts" when voicesBin is not null => InspectKitten(id, folder, preferred, tokens, voicesBin, meta),
                "matcha-tts" => InspectMatcha(id, folder, preferred, tokens, vocoders, meta),
                "vits" or "" => InspectSherpaVits(id, folder, preferred, tokens, meta),
                _ => Problem($"This is a sherpa-onnx \"{type}\" model. Cuebot speaks Piper, Kokoro, Kitten and Matcha voices."),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Problem($"The voice's files are not readable: {ex.Message}");
        }
    }

    private static bool IsVocoder(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("hifigan", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vocos", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A package may hold several models: full precision sounds best, and Matcha's "model-steps-3" is the one sherpa-onnx
    /// recommends.
    /// </summary>
    private static string PreferredModel(List<string> models) =>
        models.FirstOrDefault(m => Path.GetFileName(m).Equals("model-steps-3.onnx", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(m => !Path.GetFileName(m).Contains(".int8.", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(m).Contains(".fp16.", StringComparison.OrdinalIgnoreCase)) ?? models[0];

    private static LibraryVoice InspectPiper(string id, string folder, string model, string config)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        var root = doc.RootElement;
        var rate = root.TryGetProperty("audio", out var audio) && audio.TryGetProperty("sample_rate", out var sr) ? sr.GetInt32() : 22050;
        var language = root.TryGetProperty("language", out var lang) && lang.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
        var espeak = root.TryGetProperty("espeak", out var e) && e.TryGetProperty("voice", out var ev) ? ev.GetString() ?? "" : "";
        if (espeak.Length == 0) espeak = "en-us";
        var speakers = root.TryGetProperty("speaker_id_map", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().OrderBy(p => p.Value.GetInt32()).Select(p => new VoiceSpeaker(p.Name, p.Value.GetInt32(), espeak)).ToList()
            : [];
        var type = root.TryGetProperty("phoneme_type", out var t) ? t.GetString() ?? "espeak" : "espeak";
        List<IReadOnlyList<string>> clusters = root.TryGetProperty("vowel_clusters", out var vc) && vc.ValueKind == JsonValueKind.Array
            ? [.. vc.EnumerateArray().Select(c => (IReadOnlyList<string>)[.. c.EnumerateArray().Select(p => p.GetString() ?? "")])]
            : [];
        var problem = type is "espeak" or "text" ? null
            : $"This voice turns text into sounds with \"{type}\", which Cuebot does not have. Only voices that use espeak-ng work.";
        var format = clusters.Count > 0 ? VoiceFormat.PiperClusters : VoiceFormat.Piper;
        return new LibraryVoice(id, folder, problem is null ? format : VoiceFormat.Unknown,
            language, rate, speakers, problem)
        {
            ModelPath = model, ConfigPath = config, EspeakVoice = espeak, PhonemeType = type, VowelClusters = clusters,
        };
    }

    private static LibraryVoice InspectSherpaVits(string id, string folder, string model, string tokens, Dictionary<string, string> meta)
    {
        if (!meta.TryGetValue("sample_rate", out var rate) || !meta.TryGetValue("comment", out var comment))
            return new LibraryVoice(id, folder, VoiceFormat.Unknown, "", 0, [], "This model lacks the settings sherpa-onnx voices carry (sample rate, kind of voice). "
                + "If it is a Piper voice, put its .onnx.json file next to it.");
        if (!comment.Contains("piper", StringComparison.OrdinalIgnoreCase))
            return new LibraryVoice(id, folder, VoiceFormat.Unknown, "", 0, [], $"This is a sherpa-onnx VITS model of another kind ({comment}). "
                + "Cuebot speaks sherpa-onnx Piper (vits-piper-*), Kokoro, Kitten and Matcha voices.");
        var espeak = meta.GetValueOrDefault("voice", "en-us");
        var speakers = int.TryParse(meta.GetValueOrDefault("n_speakers", "1"), out var n) && n > 1
            ? Enumerable.Range(0, n).Select(i => new VoiceSpeaker(i.ToString(CultureInfo.InvariantCulture), i, espeak)).ToList()
            : [];
        return new LibraryVoice(id, folder, VoiceFormat.SherpaVits, LanguageCode(meta), int.Parse(rate, CultureInfo.InvariantCulture), speakers, null)
        {
            ModelPath = model, TokensPath = tokens, EspeakVoice = espeak,
        };
    }

    /// <summary>
    /// Kokoro speaker names start with their language: "af_heart" is American English. These are the espeak-ng voices
    /// (as piper-phonemize names them) for the languages Kokoro speaks through espeak-ng. Japanese (j) and Mandarin (z)
    /// speakers were trained on other phonemizers, so they are left out.
    /// </summary>
    internal static readonly IReadOnlyDictionary<char, string> KokoroLanguages = new Dictionary<char, string>
    {
        ['a'] = "en-us", ['b'] = "en-gb-x-rp", ['e'] = "es", ['f'] = "fr", ['h'] = "hi", ['i'] = "it", ['p'] = "pt-br",
    };

    private static LibraryVoice InspectKokoro(string id, string folder, string model, string tokens, string voicesBin, Dictionary<string, string> meta)
    {
        var rate = int.TryParse(meta.GetValueOrDefault("sample_rate", "24000"), CultureInfo.InvariantCulture, out var r) ? r : 24000;
        var fallback = meta.GetValueOrDefault("voice", "en-us");
        var names = SpeakerNames(meta, (int)Math.Max(1, new FileInfo(voicesBin).Length / (510 * 256 * 4)));
        var speakers = new List<VoiceSpeaker>();
        var left = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (name.Length >= 2 && name[1] is 'f' or 'm' && char.IsAsciiLetterLower(name[0]))
            {
                if (KokoroLanguages.TryGetValue(name[0], out var espeak)) speakers.Add(new VoiceSpeaker(name, i, espeak));
                else left.Add(name);
            }
            else speakers.Add(new VoiceSpeaker(name, i, fallback));
        }
        if (speakers.Count == 0)
            return new LibraryVoice(id, folder, VoiceFormat.Unknown, "", 0, [], "None of this Kokoro voice's speakers use a language Cuebot can turn into sounds (Japanese and Mandarin need other phonemizers).");
        return new LibraryVoice(id, folder, VoiceFormat.Kokoro, LanguageCode(meta), rate, speakers, null)
        {
            ModelPath = model, TokensPath = tokens, VoicesPath = voicesBin, EspeakVoice = fallback,
            Note = left.Count == 0 ? null : $"{left.Count} Japanese and Mandarin speakers are left out: Cuebot cannot turn text into their sounds.",
        };
    }

    private static LibraryVoice InspectKitten(string id, string folder, string model, string tokens, string voicesBin, Dictionary<string, string> meta)
    {
        var rate = int.TryParse(meta.GetValueOrDefault("sample_rate", "24000"), CultureInfo.InvariantCulture, out var r) ? r : 24000;
        var espeak = meta.GetValueOrDefault("voice", "en-us");
        var n = int.TryParse(meta.GetValueOrDefault("n_speakers", "1"), out var count) ? count : 1;
        var speakers = SpeakerNames(meta, n).Select((name, i) => new VoiceSpeaker(name, i, espeak)).ToList();
        return new LibraryVoice(id, folder, VoiceFormat.Kitten, LanguageCode(meta), rate, speakers, null)
        {
            ModelPath = model, TokensPath = tokens, VoicesPath = voicesBin, EspeakVoice = espeak,
        };
    }

    private static LibraryVoice InspectMatcha(string id, string folder, string model, string tokens, List<string> vocoders, Dictionary<string, string> meta)
    {
        var rate = int.TryParse(meta.GetValueOrDefault("sample_rate", "22050"), CultureInfo.InvariantCulture, out var r) ? r : 22050;
        if (meta.GetValueOrDefault("has_espeak", "0") != "1" || meta.GetValueOrDefault("jieba", "0") == "1")
            return new LibraryVoice(id, folder, VoiceFormat.Unknown, "", 0, [], $"This Matcha voice ({meta.GetValueOrDefault("language", "?")}) turns text into sounds with a "
                + "word list Cuebot does not use; only Matcha voices that use espeak-ng work.");
        if (vocoders.Count == 0)
            return new LibraryVoice(id, folder, VoiceFormat.Unknown, "", 0, [], $"This Matcha voice needs a vocoder. Import its package with \"Import voice...\" and "
                + $"Cuebot adds one, or put {Vocoder.FileName} from sherpa-onnx's vocoder-models in this folder.");
        var espeak = meta.GetValueOrDefault("voice", "en-us");
        var n = int.TryParse(meta.GetValueOrDefault("n_speakers", "1"), out var count) ? count : 1;
        var speakers = n > 1 ? Enumerable.Range(0, n).Select(i => new VoiceSpeaker(i.ToString(CultureInfo.InvariantCulture), i, espeak)).ToList() : [];
        var vocoder = vocoders.FirstOrDefault(v => Path.GetFileName(v).Equals(Vocoder.FileName, StringComparison.OrdinalIgnoreCase)) ?? vocoders[0];
        return new LibraryVoice(id, folder, VoiceFormat.Matcha, LanguageCode(meta), rate, speakers, null)
        {
            ModelPath = model, TokensPath = tokens, VocoderPath = vocoder, EspeakVoice = espeak,
        };
    }

    private static List<string> SpeakerNames(Dictionary<string, string> meta, int count) =>
        meta.TryGetValue("speaker_names", out var names) && names.Length > 0
            ? [.. names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
            : [.. Enumerable.Range(0, count).Select(i => i.ToString(CultureInfo.InvariantCulture))];

    /// <summary>sherpa-onnx stores a language name ("English"); voice pickers want a code where one is known.</summary>
    internal static string LanguageCode(Dictionary<string, string> meta)
    {
        var voice = meta.GetValueOrDefault("voice", "");
        if (voice.Length == 0) return meta.GetValueOrDefault("language", "");
        var parts = voice.Split('-', 2);
        return parts.Length == 2 && parts[1].Length == 2 ? $"{parts[0]}_{parts[1].ToUpperInvariant()}" : voice;
    }

    // ---------------------------------------------------------------- sherpa-onnx needs

    /// <summary>The private-use characters standing for a Piper voice's vowel clusters in the tokens.txt Cuebot writes.</summary>
    internal const int ClusterCodepoint = 0xE000;

    /// <summary>What <see cref="PrepareForSherpa"/> made: the tokens file and the clusters as (phonemes, character) pairs.</summary>
    internal sealed record SherpaPreparation(string Tokens, IReadOnlyList<(IReadOnlyList<string> Phonemes, string Symbol)> Clusters);

    /// <summary>
    /// Makes a Piper voice speakable by sherpa-onnx: writes <c>.cuebot/tokens.txt</c> from the voice's phoneme map
    /// (vowel clusters become private-use characters, see <see cref="ClusterCodepoint"/>) and adds sherpa-onnx's
    /// metadata to the model when it lacks it (<see cref="AddSherpaMetadata"/>). Done once per voice: the result is kept
    /// until the voice's files change or the library is refreshed.
    /// </summary>
    internal SherpaPreparation PrepareForSherpa(LibraryVoice voice)
    {
        if (voice.ConfigPath is null || voice.ModelPath is null) throw new InvalidOperationException("Only Piper voices need preparing.");
        var stamp = File.GetLastWriteTimeUtc(voice.ConfigPath);
        if (_prepared.TryGetValue(voice.ConfigPath, out var known) && known.Stamp == stamp && File.Exists(known.Result.Tokens)) return known.Result;
        var result = Prepare(voice);
        _prepared[voice.ConfigPath] = (stamp, result);
        return result;
    }

    private static SherpaPreparation Prepare(LibraryVoice voice)
    {
        var dir = Path.Combine(voice.Folder, ".cuebot");
        var tokens = Path.Combine(dir, "tokens.txt");
        var config = voice.ConfigPath ?? throw new InvalidOperationException($"{voice.Id} has no Piper config.");
        var modelPath = voice.ModelPath ?? throw new InvalidOperationException($"{voice.Id} has no model.");
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        var map = doc.RootElement.GetProperty("phoneme_id_map");
        var text = new StringBuilder();
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in map.EnumerateObject())
        {
            var symbol = entry.Name;
            if (symbol.EnumerateRunes().Count() != 1)
            {
                symbol = char.ConvertFromUtf32(ClusterCodepoint + symbols.Count);
                symbols[entry.Name] = symbol;
            }
            text.Append(symbol).Append(' ').Append(entry.Value[0].GetInt32().ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        System.IO.Directory.CreateDirectory(dir);
        if (!File.Exists(tokens) || File.ReadAllText(tokens) != text.ToString())
            File.WriteAllText(tokens, text.ToString(), new UTF8Encoding(false));

        if (!OnnxMetadata.Read(modelPath).ContainsKey("sample_rate"))
            AddSherpaMetadata(modelPath, dir, new Dictionary<string, string>
            {
                ["model_type"] = "vits",
                ["comment"] = "piper",
                ["language"] = voice.LanguageCode,
                ["voice"] = voice.EspeakVoice,
                ["has_espeak"] = "1",
                ["n_speakers"] = Math.Max(1, voice.Speakers.Count).ToString(CultureInfo.InvariantCulture),
                ["sample_rate"] = voice.SampleRate.ToString(CultureInfo.InvariantCulture),
            });
        var clusters = voice.VowelClusters
            .Select(c => ((IReadOnlyList<string>)c, symbols.GetValueOrDefault(string.Concat(c))))
            .Where(c => c.Item2 is not null)
            .Select(c => (c.Item1, c.Item2!))
            .ToList();
        return new SherpaPreparation(tokens, clusters);
    }

    /// <summary>
    /// Adds metadata to the library's copy of a model without ever leaving it half-written: the model is copied next to
    /// it (in <paramref name="workDirectory"/>, the same disk), the entries are appended to the copy, and the copy replaces
    /// the model in one rename. The model's own bytes stay as they were; Piper reads the file as before.
    /// </summary>
    internal static void AddSherpaMetadata(string model, string workDirectory, IReadOnlyDictionary<string, string> entries)
    {
        System.IO.Directory.CreateDirectory(workDirectory);
        var temp = Path.Combine(workDirectory, Path.GetFileName(model) + ".tmp");
        try
        {
            File.Copy(model, temp, overwrite: true);
            OnnxMetadata.Append(temp, entries);
            OnnxMetadata.Read(temp); // the copy must read back before it replaces anything
            File.Move(temp, model, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // ---------------------------------------------------------------- moving voices in

    /// <summary>File.Move; tests replace it to make a move fail half-way.</summary>
    internal static Action<string, string> MoveFile { get; set; } = (from, to) => File.Move(from, to);

    /// <summary>
    /// Moves the voices Cuebot 1.0.6 and before downloaded (<c>piper/voices/*.onnx</c> and <c>.onnx.json</c>) into the
    /// library, one folder each, so nobody has to download them again. Both files go into a staging folder first, which
    /// becomes the voice's folder only once both are there; if the second move fails, the first is moved back, so a voice
    /// is never left half-moved. Run at start-up; does nothing once everything has moved.
    /// </summary>
    public int MigrateLegacyPiperVoices(string legacyPiperDirectory)
    {
        var legacy = Path.Combine(legacyPiperDirectory, "voices");
        var moved = 0;
        if (System.IO.Directory.Exists(legacy))
        {
            foreach (var model in System.IO.Directory.EnumerateFiles(legacy, "*.onnx").ToList())
            {
                var config = model + ".json";
                if (!File.Exists(config)) continue;
                var key = Path.GetFileNameWithoutExtension(model);
                var target = Path.Combine(Directory, SafeName(key));
                if (System.IO.Directory.Exists(target)) continue;
                var staging = Path.Combine(Directory, ".migrate-" + Guid.NewGuid().ToString("N"));
                var stagedModel = Path.Combine(staging, Path.GetFileName(model));
                try
                {
                    System.IO.Directory.CreateDirectory(staging);
                    MoveFile(model, stagedModel);
                    try
                    {
                        MoveFile(config, Path.Combine(staging, Path.GetFileName(config)));
                    }
                    catch (IOException)
                    {
                        File.Move(stagedModel, model); // put it back: try again at the next start
                        throw;
                    }
                    MarkDownloaded(staging);
                    System.IO.Directory.Move(staging, target);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn(LogSource, $"Could not move Piper voice {key} into the voice library: {ex.Message}");
                }
                finally
                {
                    if (System.IO.Directory.Exists(staging) && !File.Exists(stagedModel)) System.IO.Directory.Delete(staging, recursive: true);
                }
            }
            TryDeleteEmpty(legacy);
        }
        var compat = Path.Combine(legacyPiperDirectory, "compat");
        if (System.IO.Directory.Exists(compat)) System.IO.Directory.Delete(compat, recursive: true);
        if (moved > 0)
        {
            _log.Info(LogSource, $"Moved {moved} Piper voice(s) into the voice library ({Directory})");
            Refresh();
        }
        return moved;
    }

    /// <summary>
    /// Imports voices from files the user picked: Piper voices (<c>.onnx</c> with its <c>.onnx.json</c>, either picked
    /// or found next to it) and voice packages (<c>.tar.bz2</c>, <c>.tar.gz</c>). Each is copied or unpacked into a
    /// staging folder and reaches the library only once Cuebot recognises it as a voice (a Matcha voice without a vocoder
    /// gets <see cref="Vocoder"/>). <paramref name="move"/> deletes the picked files, but only after their voice is in
    /// the library: a refused or cancelled import never loses them. Returns the new voices' ids.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportAsync(IReadOnlyList<string> paths, bool move, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var imported = new List<string>();
        var pairs = new List<(string Model, string Config)>();
        var archives = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (VoiceArchive.IsArchive(path)) archives.Add(path);
            else if (path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) pairs.Add((path, path + ".json"));
            else if (path.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase)) pairs.Add((path[..^5], path));
            else throw new InvalidOperationException($"Cuebot imports .onnx voices (with their .onnx.json) and .tar.bz2 or .tar.gz voice packages, not {Path.GetFileName(path)}.");
        }
        try
        {
            foreach (var (model, config) in pairs.Distinct())
            {
                if (!File.Exists(model)) throw new InvalidOperationException($"{Path.GetFileName(config)} needs its model {Path.GetFileName(model)} next to it.");
                if (!File.Exists(config)) throw new InvalidOperationException($"{Path.GetFileName(model)} needs its settings file {Path.GetFileName(config)} next to it.");
                progress?.Report($"Importing {Path.GetFileName(model)}");
                imported.Add(await StageAsync(Path.GetFileNameWithoutExtension(model), async staging =>
                {
                    await CopyAsync(model, Path.Combine(staging, Path.GetFileName(model)), ct).ConfigureAwait(false);
                    await CopyAsync(config, Path.Combine(staging, Path.GetFileName(config)), ct).ConfigureAwait(false);
                    return staging;
                }, progress, ct).ConfigureAwait(false));
                if (move)
                {
                    File.Delete(model);
                    File.Delete(config);
                }
            }
            foreach (var archive in archives)
            {
                progress?.Report($"Unpacking {Path.GetFileName(archive)}");
                imported.Add(await StageAsync(VoiceArchive.BaseName(archive), async staging =>
                {
                    await VoiceArchive.ExtractAsync(archive, staging, ct).ConfigureAwait(false);
                    // Packages usually hold one top folder (vits-piper-en_US-glados-high/...): that folder is the voice.
                    var entries = System.IO.Directory.GetFileSystemEntries(staging);
                    return entries.Length == 1 && System.IO.Directory.Exists(entries[0]) ? entries[0] : staging;
                }, progress, ct).ConfigureAwait(false));
                if (move) File.Delete(archive);
            }
        }
        finally
        {
            if (imported.Count > 0) Refresh();
        }
        return imported;
    }

    private async Task<string> StageAsync(string name, Func<string, Task<string>> fill, IProgress<string>? progress, CancellationToken ct)
    {
        var staging = Path.Combine(Directory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            var content = await fill(staging).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // A package's top folder names the voice (vits-piper-en_US-glados-high); otherwise the file it came from does.
            if (content != staging) name = Path.GetFileName(content);
            await AddVocoderIfMissingAsync(content, progress, ct).ConfigureAwait(false);
            var voice = Inspect(content);
            if (voice.Problem is not null) throw new InvalidOperationException($"{name}: {voice.Problem}");
            var id = UniqueName(name);
            System.IO.Directory.Move(content, Path.Combine(Directory, id));
            _log.Info(LogSource, $"Imported voice {id} ({voice.FormatName})");
            return id;
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>sherpa-onnx publishes Matcha voices without their vocoder; Cuebot fetches the pinned one into the voice's folder.</summary>
    private async Task AddVocoderIfMissingAsync(string folder, IProgress<string>? progress, CancellationToken ct)
    {
        if (_http is null) return;
        var onnx = System.IO.Directory.EnumerateFiles(folder, "*.onnx").ToList();
        if (onnx.Any(IsVocoder) || !File.Exists(Path.Combine(folder, "tokens.txt"))) return;
        var isMatcha = onnx.Any(m =>
        {
            try
            {
                return OnnxMetadata.Read(m).GetValueOrDefault("model_type") == "matcha-tts";
            }
            catch (InvalidDataException)
            {
                return false;
            }
        });
        if (!isMatcha) return;
        progress?.Report($"Downloading the vocoder ({Vocoder.FileName}, {Vocoder.Size / 1_000_000d:0.0} MB)");
        var target = Path.Combine(folder, Vocoder.FileName);
        using var response = await _http.GetAsync(Vocoder.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Downloading the vocoder failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length != Vocoder.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), Vocoder.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{Vocoder.FileName} does not match the expected checksum; the voice was not imported.");
        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
    }

    private static async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        await using var source = File.OpenRead(from);
        await using var target = File.Create(to);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private string UniqueName(string name)
    {
        var safe = SafeName(name);
        var candidate = safe;
        for (var i = 2; System.IO.Directory.Exists(Path.Combine(Directory, candidate)); i++) candidate = $"{safe} ({i})";
        return candidate;
    }

    /// <summary>A folder name made from a voice name: no path separators or characters Windows refuses, never "." or "..".</summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string([.. name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).TrimStart('.').TrimEnd('.', ' ');
        return safe.Length == 0 ? "voice" : safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Overwritten next time.
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (!System.IO.Directory.EnumerateFileSystemEntries(directory).Any()) System.IO.Directory.Delete(directory);
        }
        catch (IOException)
        {
            // Left for the user.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
