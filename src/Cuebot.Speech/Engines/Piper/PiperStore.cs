using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>One Piper program download: the 2023.11.14-2 release, the last one with ready-to-run programs (MIT).</summary>
public sealed record PiperRelease(string FileName, long SizeBytes, string Sha256)
{
    public const string Tag = "2023.11.14-2";

    public Uri Url => new($"https://github.com/rhasspy/piper/releases/download/{Tag}/{FileName}");

    /// <summary>The release for this computer, or null when Piper has no program for it.</summary>
    public static PiperRelease? ForThisComputer()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows()) return new("piper_windows_amd64.zip", 22477236, "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea");
        if (OperatingSystem.IsMacOS())
            return arm
                ? new("piper_macos_aarch64.tar.gz", 19146957, "6b1eb03b3735946cb35216e063e7eebcc33a6bbf5dd96ec0217959bf1cdcb0cc")
                : new("piper_macos_x64.tar.gz", 19146927, "ced85c0a3df13945b1e623b878a48fdc2854d5c485b4b67f62857cf551deaf8b");
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

/// <summary>A downloaded voice: its model file and what its config says.</summary>
public sealed record PiperInstalledVoice(string Key, string ModelPath, int SampleRate, string LanguageCode, IReadOnlyList<string> Speakers);

/// <summary>
/// Piper's program and voices in the data folder (<c>piper/program</c>, <c>piper/voices</c>). Nothing is bundled: the
/// program comes from Piper's GitHub release and is checked against a pinned SHA-256; voices come from Piper's voice
/// catalog and are checked against the size and MD5 it lists. Only a checked file is moved into place.
/// </summary>
public sealed class PiperStore
{
    private const string LogSource = "Speech";
    internal const string VoicesBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/";
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PiperStore(string directory, HttpClient http, AppLog log)
    {
        Directory = directory;
        _http = http;
        _log = log;
    }

    public string Directory { get; }

    public string ProgramDirectory => Path.Combine(Directory, "program");

    public string VoicesDirectory => Path.Combine(Directory, "voices");

    private string CatalogPath => Path.Combine(Directory, "voices.json");

    /// <summary>Raised (on a background thread) when the program or a voice was added or removed.</summary>
    public event Action? Changed;

    /// <summary>The program to run: the user's choice when it exists, else the downloaded one.</summary>
    public string? FindProgram(string custom)
    {
        if (custom.Trim().Length > 0 && File.Exists(custom.Trim())) return custom.Trim();
        var downloaded = Path.Combine(ProgramDirectory, "piper", OperatingSystem.IsWindows() ? "piper.exe" : "piper");
        return File.Exists(downloaded) ? downloaded : null;
    }

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
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("files", out var files)) continue;
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

    public bool IsInstalled(string key) => File.Exists(ModelPath(key)) && File.Exists(ModelPath(key) + ".json");

    private string ModelPath(string key) => Path.Combine(VoicesDirectory, Path.GetFileName(key) + ".onnx");

    /// <summary>Downloads a voice (its model and config), checking both against the catalog.</summary>
    public async Task DownloadVoiceAsync(PiperCatalogVoice voice, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(VoicesDirectory);
            _log.Info(LogSource, $"Downloading Piper voice {voice.Key}");
            var model = ModelPath(voice.Key);
            await DownloadCheckedAsync(new Uri(VoicesBaseUrl + voice.Config.Path), model + ".json", voice.Config.SizeBytes,
                HashAlgorithmName.MD5, voice.Config.Md5, null, ct).ConfigureAwait(false);
            await DownloadCheckedAsync(new Uri(VoicesBaseUrl + voice.Model.Path), model, voice.Model.SizeBytes,
                HashAlgorithmName.MD5, voice.Model.Md5, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public void DeleteVoice(string key)
    {
        TryDelete(ModelPath(key));
        TryDelete(ModelPath(key) + ".json");
        Changed?.Invoke();
    }

    /// <summary>The voices in the voices folder that have both files.</summary>
    public IReadOnlyList<PiperInstalledVoice> InstalledVoices()
    {
        if (!System.IO.Directory.Exists(VoicesDirectory)) return [];
        var list = new List<PiperInstalledVoice>();
        foreach (var model in System.IO.Directory.EnumerateFiles(VoicesDirectory, "*.onnx").Order(StringComparer.OrdinalIgnoreCase))
        {
            var config = model + ".json";
            if (!File.Exists(config)) continue;
            try
            {
                list.Add(ReadVoice(model, File.ReadAllText(config)));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _log.Warn(LogSource, $"Skipping Piper voice {Path.GetFileName(model)}: its .json file is not readable ({ex.Message})");
            }
        }
        return list;
    }

    internal static PiperInstalledVoice ReadVoice(string modelPath, string configJson)
    {
        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        var rate = root.TryGetProperty("audio", out var audio) && audio.TryGetProperty("sample_rate", out var sr) ? sr.GetInt32() : 22050;
        var language = root.TryGetProperty("language", out var lang) && lang.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
        var speakers = root.TryGetProperty("speaker_id_map", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().OrderBy(p => p.Value.GetInt32()).Select(p => p.Name).ToList()
            : [];
        return new PiperInstalledVoice(Path.GetFileNameWithoutExtension(modelPath), modelPath, rate, language, speakers);
    }

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
