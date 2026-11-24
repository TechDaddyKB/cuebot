using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Scripting;

namespace Cuebot.Speech;

/// <summary>
/// The voice engine: sherpa-onnx in a Python environment of its own (<c>voice-engine</c> in the data folder), run as a
/// separate process (<c>cuebot_voice_host.py</c>) that keeps voices loaded between lines. It speaks every voice format
/// of the <see cref="VoiceLibrary"/> on Windows, macOS and Linux. Nothing is bundled: Python (python-build-standalone)
/// and the packages (<see cref="VoiceEnginePins"/>) are downloaded when the user asks, each checked against a pinned
/// SHA-256. The GPL phonemizer (piper_phonemize) only ever runs inside that process, never in Cuebot.
/// </summary>
public sealed class VoiceEngine : IAsyncDisposable
{
    private const string LogSource = "Speech";
    private const string HostScript = "cuebot_voice_host.py";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly AppLog _log;
    private readonly PythonLayout _layout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private readonly Queue<string> _stderr = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private int _nextId;
    private bool? _installed;

    public VoiceEngine(string directory, HttpClient http, AppLog log)
    {
        Directory = directory;
        _http = http;
        _log = log;
        _layout = new PythonLayout(directory);
    }

    public string Directory { get; }

    private string StampPath => Path.Combine(Directory, "installed.txt");

    /// <summary>The running host's process id (tests check a skipped line does not restart it).</summary>
    internal int? HostProcessId => _process is { HasExited: false } p ? p.Id : null;

    private string ScriptPath => Path.Combine(Directory, HostScript);

    /// <summary>Raised (on a background thread) after the engine was installed or removed.</summary>
    public event Action? Changed;

    /// <summary>The wheels for this computer, or null when the engine has no build for it.</summary>
    public static IReadOnlyList<VoiceWheel>? WheelsForThisComputer() =>
        PinnedPython.ForThisComputer() is null ? null : VoiceEnginePins.For(PinnedPython.CurrentTarget());

    public static bool CanInstall => WheelsForThisComputer() is not null;

    /// <summary>Download size of everything the engine needs, in MB, for messages.</summary>
    public static int DownloadMegabytes =>
        (int)Math.Round(((PinnedPython.ForThisComputer()?.Size ?? 0) + (WheelsForThisComputer()?.Sum(w => w.Size) ?? 0)) / 1_000_000d);

    /// <summary>Read from disk once, then kept until the next <see cref="InstallAsync"/> (it is asked for on every line spoken).</summary>
    public bool IsInstalled => _installed ??=
        File.Exists(_layout.EnvironmentExecutable) && File.Exists(StampPath) && File.ReadAllText(StampPath).Trim() == VoiceEnginePins.Stamp;

    /// <summary>Downloads Python and the voice packages, checks them, installs them and makes sure the engine starts.</summary>
    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var wheels = WheelsForThisComputer() ?? throw new PlatformNotSupportedException("The voice engine has no build for this computer.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Stop();
            _installed = false;
            System.IO.Directory.CreateDirectory(Directory);
            TryDelete(StampPath);
            if (!File.Exists(_layout.EnvironmentExecutable) || _layout.ReadEnvironmentVersion() != PinnedPython.Version)
                await new PythonInstaller(_http, _layout).InstallAsync(progress, ct).ConfigureAwait(false);

            var folder = Path.Combine(Directory, "wheels");
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var files = new List<string>();
                foreach (var wheel in wheels)
                {
                    progress?.Report($"Downloading {wheel.FileName} ({wheel.Size / 1_000_000d:0} MB)");
                    var path = Path.Combine(folder, wheel.FileName);
                    await DownloadCheckedAsync(wheel, path, ct).ConfigureAwait(false);
                    files.Add(path);
                }
                progress?.Report("Installing the voice engine");
                var pip = await Cuebot.Scripting.ProcessRunner.RunAsync(_layout.EnvironmentExecutable,
                    ["-m", "pip", "install", "--no-index", "--no-deps", "--force-reinstall", "--no-warn-script-location", .. files],
                    Directory, null, ct).ConfigureAwait(false);
                if (pip.ExitCode != 0) throw new InvalidOperationException($"Installing the voice engine failed: {pip.LastErrorLine}");
            }
            finally
            {
                if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
            }

            progress?.Report("Starting the voice engine");
            var hello = await SendLockedAsync(new JsonObject { ["op"] = "hello" }, ct).ConfigureAwait(false);
            if (hello["phonemize"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The voice engine started, but its phonemizer did not load.");
            await File.WriteAllTextAsync(StampPath, VoiceEnginePins.Stamp, ct).ConfigureAwait(false);
            _installed = null;
            _log.Info(LogSource, $"Voice engine installed ({VoiceEnginePins.Stamp}, Python {PinnedPython.Version})");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>Speaks one line and returns the WAV. <paramref name="request"/> holds the voice fields of the host protocol.</summary>
    public async Task<byte[]> SpeakAsync(JsonObject request, CancellationToken ct)
    {
        if (!IsInstalled) throw new InvalidOperationException("The voice engine is not installed.");
        request["op"] = "speak";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reply = await SendLockedAsync(request, ct).ConfigureAwait(false);
            return Convert.FromBase64String(reply["wav"]?.GetValue<string>() ?? throw new InvalidOperationException("The voice engine sent no sound."));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends one request and waits for its reply. A caller that gives up (a skipped line) only stops waiting: the host
    /// finishes that line, its reply is dropped when it arrives, and the voice stays loaded for the next line. The host
    /// is restarted only when it stops answering (<see cref="RequestTimeout"/>) or its pipe breaks.
    /// </summary>
    private async Task<JsonObject> SendLockedAsync(JsonObject request, CancellationToken ct)
    {
        var process = EnsureStarted();
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = reply;
        try
        {
            try
            {
                // Never cancelled half-way: a half-written line would garble the next request.
                await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                Stop();
                throw new InvalidOperationException($"The voice engine stopped{LastError()} ({ex.Message}).", ex);
            }
            JsonObject result;
            try
            {
                result = await reply.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Stop();
                throw new TimeoutException("The voice engine took too long and was restarted.");
            }
            if (result["ok"]?.GetValue<bool>() == true) return result;
            throw new InvalidOperationException($"The voice engine could not speak: {result["error"]?.GetValue<string>() ?? "unknown error"}");
        }
        finally
        {
            lock (_pending) _pending.Remove(id);
        }
    }

    /// <summary>Reads the host's replies for as long as it runs and hands each to the request waiting for it.</summary>
    private async Task ReadRepliesAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonObject reply;
                try
                {
                    reply = JsonNode.Parse(line) as JsonObject ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    continue; // something a library printed; not ours
                }
                if (reply["id"] is not { } id || id.GetValueKind() != JsonValueKind.Number) continue;
                TaskCompletionSource<JsonObject>? waiting;
                lock (_pending) _pending.TryGetValue(id.GetValue<int>(), out waiting);
                waiting?.TrySetResult(reply); // nobody waiting: a line the caller gave up on
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was stopped.
        }
        var stopped = new InvalidOperationException($"The voice engine stopped{LastError()}.");
        lock (_pending)
            foreach (var waiting in _pending.Values) waiting.TrySetException(stopped);
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false } running) return running;
        _process?.Dispose();
        WriteHostScript();
        var info = Cuebot.Scripting.ProcessRunner.CreateStartInfo(_layout.EnvironmentExecutable, ["-u", "-s", ScriptPath], Directory);
        info.Environment["PYTHONNOUSERSITE"] = "1";
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the voice engine.");
        lock (_stderr) _stderr.Clear();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > 20) _stderr.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        _process = process;
        _ = ReadRepliesAsync(process);
        return process;
    }

    private string LastError()
    {
        lock (_stderr) return _stderr.Count == 0 ? "" : $": {_stderr.Last()}";
    }

    private void WriteHostScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cuebot.Speech." + HostScript)
            ?? throw new InvalidOperationException("The voice engine script is missing from Cuebot.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        if (!File.Exists(ScriptPath) || File.ReadAllText(ScriptPath) != text) File.WriteAllText(ScriptPath, text, new UTF8Encoding(false));
    }

    private async Task DownloadCheckedAsync(VoiceWheel wheel, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, wheel.Url);
        request.Headers.UserAgent.ParseAdd("Cuebot");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Downloading {wheel.FileName} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > wheel.Size) throw new InvalidDataException($"{wheel.FileName} is larger than expected; it was not installed.");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        if (received != wheel.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), wheel.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{wheel.FileName} does not match the expected checksum; it was not installed.");
    }

    private void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                // Windows keeps the environment's files locked until the process is really gone (a repair reinstalls them).
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _process.Dispose();
        _process = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Overwritten on the next install.
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
