using System.Collections.Frozen;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>One Piper program download: the 2023.11.14-2 release, the last one with ready-to-run programs (MIT).</summary>
public sealed record PiperRelease(string FileName, long SizeBytes, string Sha256)
{
    public const string Tag = "2023.11.14-2";

    public Uri Url => new($"https://github.com/rhasspy/piper/releases/download/{Tag}/{FileName}");

    /// <summary>Why Piper cannot be downloaded on a Mac (see <see cref="ForThisComputer"/>).</summary>
    public const string MacReason =
        "Piper's download for Mac does not run (it is missing its libraries), so Cuebot cannot get it for you. If you "
        + "have a working piper program, choose it under Program; otherwise use another voice.";

    /// <summary>The release for this computer, or null when Piper has no program for it.</summary>
    public static PiperRelease? ForThisComputer()
    {
        if (OperatingSystem.IsWindows()) return new("piper_windows_amd64.zip", 22477236, "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea");
        // The macOS archives of this release are broken: the "aarch64" one holds an x86_64 program, and both lack
        // libespeak-ng, libpiper_phonemize and libonnxruntime (only their debug symbols are included).
        if (OperatingSystem.IsMacOS()) return null;
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => new("piper_linux_x86_64.tar.gz", 26460462, "a50cb45f355b7af1f6d758c1b360717877ba0a398cc8cbe6d2a7a3a26e225992"),
                Architecture.Arm64 => new("piper_linux_aarch64.tar.gz", 26004717, "fea0fd2d87c54dbc7078d0f878289f404bd4d6eea6e7444a77835d1537ab88eb"),
                _ => null,
            };
        return null;
    }
}

/// <summary>A file of a voice in Piper's catalog, with the size and MD5 the catalog lists.</summary>
public sealed record PiperVoiceFile(string Path, long SizeBytes, string Md5);

/// <summary>A voice from Piper's catalog (huggingface.co/rhasspy/piper-voices, voices.json).</summary>
public sealed record PiperCatalogVoice(string Key, string Name, string LanguageCode, string Language, string Country, string Quality,
    int Speakers, PiperVoiceFile Model, PiperVoiceFile Config)
{
    public long SizeBytes => Model.SizeBytes + Config.SizeBytes;

    /// <summary>What the voice picker shows, e.g. "English (United States): Lessac, medium".</summary>
    public string Display =>
        $"{Language}{(Country.Length > 0 ? $" ({Country})" : "")}: {char.ToUpperInvariant(Name[0])}{Name[1..]}, {Quality}" +
        (Speakers > 1 ? $", {Speakers} voices" : "");
}

/// <summary>
/// Piper's voice catalog and the Piper program. Voices from the catalog go into the shared <see cref="VoiceLibrary"/>,
/// checked against the size and MD5 the catalog lists, and reach their folder only once both files are complete. The
/// Piper 2023.11.14-2 program (<c>piper/program</c>, checked against a pinned SHA-256) is what Cuebot 1.0.6 and
/// before downloaded; it still speaks classic Piper voices where it is installed (see <see cref="VoiceLibraryEngine"/>).
/// </summary>
public sealed class PiperStore
{
    private const string LogSource = "Speech";

    internal const string VoicesBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";

    /// <summary>
    /// Catalog voices that turn text into phonemes with something other than espeak-ng (Hebrew, Japanese, Lithuanian,
    /// Thai, Chinese pinyin). Neither the voice engine nor the Piper program has those, so they are hidden.
    /// </summary>
    internal static readonly FrozenSet<string> UnsupportedVoices = new[]
    {
        "he_IL-saspeech-medium", "ja_JP-hi_fi_captain-medium", "lt_LT-ingute-medium", "lt_LT-reginute1-medium",
        "th_TH-tsync2-medium", "zh_CN-chaowen-medium", "zh_CN-xiao_ya-medium",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly AppLog _log;
    private readonly VoiceLibrary _library;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PiperStore(string directory, HttpClient http, AppLog log, VoiceLibrary library)
    {
        Directory = directory;
        _http = http;
        _log = log;
        _library = library;
    }

    public string Directory { get; }

    public string ProgramDirectory => Path.Combine(Directory, "program");

    private string CatalogPath => Path.Combine(Directory, "voices.json");

    /// <summary>Raised (on a background thread) when the program was installed. Voices raise <see cref="VoiceLibrary.Changed"/>.</summary>
    public event Action? Changed;

    /// <summary>The program to run: the user's choice when it exists, else the downloaded one.</summary>
    public string? FindProgram(string custom)
    {
        if (custom.Trim().Length > 0 && File.Exists(custom.Trim())) return custom.Trim();
        return File.Exists(DownloadedProgram) ? DownloadedProgram : null;
    }

    private string DownloadedProgram => Path.Combine(ProgramDirectory, "piper", OperatingSystem.IsWindows() ? "piper.exe" : "piper");

    /// <summary>True for the program <see cref="InstallProgramAsync"/> put in place (Piper <see cref="PiperRelease.Tag"/>).</summary>
    internal bool IsDownloadedProgram(string program) =>
        string.Equals(Path.GetFullPath(program), Path.GetFullPath(DownloadedProgram),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads and unpacks Piper's program for this computer (about 20-26 MB).</summary>
    public async Task InstallProgramAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var release = PiperRelease.ForThisComputer() ?? throw new PlatformNotSupportedException("Piper has no program for this computer.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var archive = Path.Combine(Directory, release.FileName);
            _log.Info(LogSource, $"Downloading Piper {PiperRelease.Tag} ({release.FileName})");
            await DownloadCheckedAsync(release.Url, archive, release.SizeBytes, HashAlgorithmName.SHA256, release.Sha256, progress, ct).ConfigureAwait(false);
            try
            {
                var staging = ProgramDirectory + ".new";
                if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
                System.IO.Directory.CreateDirectory(staging);
                if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(archive, staging);
                }
                else
                {
                    await using var file = File.OpenRead(archive);
                    await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true, ct).ConfigureAwait(false);
                }
                if (!OperatingSystem.IsWindows())
                    foreach (var name in new[] { "piper", "espeak-ng", "piper_phonemize" })
                    {
                        var path = Path.Combine(staging, "piper", name);
                        if (File.Exists(path))
                            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                    }
                if (System.IO.Directory.Exists(ProgramDirectory)) System.IO.Directory.Delete(ProgramDirectory, true);
                System.IO.Directory.Move(staging, ProgramDirectory);
            }
            finally
            {
                TryDelete(archive);
            }
            _log.Info(LogSource, "Piper is installed");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>Piper's voice catalog, cached for a week (an old copy is used when offline).</summary>
    public async Task<IReadOnlyList<PiperCatalogVoice>> GetCatalogAsync(CancellationToken ct = default)
    {
        var cached = new FileInfo(CatalogPath);
        if (!cached.Exists || DateTime.UtcNow - cached.LastWriteTimeUtc > CatalogMaxAge)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var json = await _http.GetStringAsync(new Uri(VoicesBaseUrl + "voices.json"), ct).ConfigureAwait(false);
                ParseCatalog(json); // only keep a copy that parses
                await File.WriteAllTextAsync(CatalogPath, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (!cached.Exists) throw new InvalidOperationException($"Could not load Piper's voice list: {ex.Message}", ex);
                _log.Warn(LogSource, $"Using the saved Piper voice list ({ex.Message})");
            }
        }
        return ParseCatalog(await File.ReadAllTextAsync(CatalogPath, ct).ConfigureAwait(false));
    }

    internal static IReadOnlyList<PiperCatalogVoice> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PiperCatalogVoice>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            var v = entry.Value;
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("files", out var files) || UnsupportedVoices.Contains(entry.Name)) continue;
            PiperVoiceFile? model = null, config = null;
            foreach (var f in files.EnumerateObject())
            {
                var file = new PiperVoiceFile(f.Name, f.Value.GetProperty("size_bytes").GetInt64(), f.Value.GetProperty("md5_digest").GetString() ?? "");
                if (f.Name.EndsWith(".onnx", StringComparison.Ordinal)) model = file;
                else if (f.Name.EndsWith(".onnx.json", StringComparison.Ordinal)) config = file;
            }
            if (model is null || config is null) continue;
            var language = v.TryGetProperty("language", out var l) ? l : default;
            string Lang(string name) => language.ValueKind == JsonValueKind.Object && language.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";
            list.Add(new PiperCatalogVoice(entry.Name, v.GetProperty("name").GetString() ?? entry.Name, Lang("code"), Lang("name_english"),
                Lang("country_english"), v.TryGetProperty("quality", out var q) ? q.GetString() ?? "" : "",
                v.TryGetProperty("num_speakers", out var n) ? n.GetInt32() : 1, model, config));
        }
        return [.. list.OrderBy(v => v.Display, StringComparer.OrdinalIgnoreCase)];
    }

    public bool IsInstalled(string key) => _library.Contains(key);

    /// <summary>
    /// Downloads a voice (its model and config) into the voice library, checking both against the catalog. The files
    /// are gathered in a staging folder first, so the library never shows half a voice.
    /// </summary>
    public async Task DownloadVoiceAsync(PiperCatalogVoice voice, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var staging = Path.Combine(_library.Directory, ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            _log.Info(LogSource, $"Downloading Piper voice {voice.Key}");
            var model = Path.Combine(staging, Path.GetFileName(voice.Key) + ".onnx");
            await DownloadCheckedAsync(new Uri(VoicesBaseUrl + voice.Config.Path), model + ".json", voice.Config.SizeBytes,
                HashAlgorithmName.MD5, voice.Config.Md5, null, ct).ConfigureAwait(false);
            await DownloadCheckedAsync(new Uri(VoicesBaseUrl + voice.Model.Path), model, voice.Model.SizeBytes,
                HashAlgorithmName.MD5, voice.Model.Md5, progress, ct).ConfigureAwait(false);
            VoiceLibrary.MarkDownloaded(staging);
            var target = Path.Combine(_library.Directory, VoiceLibrary.SafeName(voice.Key));
            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, recursive: true);
            System.IO.Directory.Move(staging, target);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
            _gate.Release();
        }
        _library.Refresh();
    }

    /// <summary>
    /// The voice config to give the Piper program: the voice's own .json, or a copy Piper 2023.11.14-2 can read (in the
    /// voice's <c>.cuebot</c> folder) when the voice lists phonemes it refuses (see <see cref="CompatibleConfig"/>).
    /// </summary>
    internal static string PrepareConfig(LibraryVoice voice)
    {
        var original = voice.ConfigPath ?? throw new InvalidOperationException("Only Piper voices have a config.");
        var compat = Path.Combine(voice.Folder, ".cuebot", "compat.onnx.json");
        if (File.Exists(compat) && File.GetLastWriteTimeUtc(compat) >= File.GetLastWriteTimeUtc(original)) return compat;
        var cleaned = CompatibleConfig(File.ReadAllText(original));
        if (cleaned is null)
        {
            TryDelete(compat);
            return original;
        }
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(compat)!);
        File.WriteAllText(compat + ".part", cleaned);
        File.Move(compat + ".part", compat, overwrite: true);
        return compat;
    }

    /// <summary>
    /// Some voices made with Piper 1.5 and later list multi-character phonemes such as "aɪ" in their config without
    /// using them (they have no "vowel_clusters", so Piper 1.5 also looks phonemes up one character at a time). Piper
    /// 2023.11.14-2 refuses multi-character phonemes and crashes (issue 61), so this returns the config without them,
    /// or null when nothing has to change. Voices that do use them are <see cref="VoiceFormat.PiperClusters"/>, which
    /// only the voice engine speaks.
    /// </summary>
    internal static string? CompatibleConfig(string configJson)
    {
        var root = JsonNode.Parse(configJson)?.AsObject() ?? throw new JsonException("The voice config is empty.");
        var changed = false;
        if (root["phoneme_id_map"] is JsonObject ids) changed |= RemoveKeys(ids, k => !IsSingleCodepoint(k));
        if (root["phoneme_map"] is JsonObject map)
            changed |= RemoveKeys(map, k => !IsSingleCodepoint(k) || map[k] is not JsonArray to
                || to.Any(p => p?.GetValueKind() != JsonValueKind.String || !IsSingleCodepoint(p.GetValue<string>())));
        if (root["inference"] is JsonObject inference && inference["phoneme_silence"] is JsonObject silence)
            changed |= RemoveKeys(silence, k => !IsSingleCodepoint(k));
        return changed ? root.ToJsonString() : null;

        static bool RemoveKeys(JsonObject obj, Func<string, bool> remove)
        {
            var keys = obj.Select(p => p.Key).Where(remove).ToList();
            foreach (var key in keys) obj.Remove(key);
            return keys.Count > 0;
        }
    }

    private static bool IsSingleCodepoint(string s) => s.Length > 0 && s.EnumerateRunes().Count() == 1;

    private async Task DownloadCheckedAsync(Uri url, string target, long size, HashAlgorithmName algorithm, string expected,
        IProgress<double>? progress, CancellationToken ct)
    {
        var part = target + ".part";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            using var hash = IncrementalHash.CreateHash(algorithm);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (received > size) throw new InvalidOperationException("The download is larger than expected.");
                    progress?.Report((double)received / size);
                }
            }
            if (received != size) throw new InvalidOperationException($"The download stopped early ({received} of {size} bytes).");
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download failed its check and was thrown away. Try again.");
            File.Move(part, target, overwrite: true);
        }
        finally
        {
            TryDelete(part);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next attempt.
        }
    }
}
