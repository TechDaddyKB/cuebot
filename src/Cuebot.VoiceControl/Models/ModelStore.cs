using System.Security.Cryptography;
using Cuebot.Core.Logging;

namespace Cuebot.VoiceControl.Models;

/// <summary>Progress of one model download.</summary>
public sealed record ModelDownloadProgress(string ModelId, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}

/// <summary>
/// Whisper models in the data folder (<c>models/whisper</c>). Downloads stream to a <c>.part</c> file while a SHA-256
/// is computed, and only a file with the catalog's size and hash is renamed into place, so a broken or tampered
/// download is never loaded. Nothing is bundled with Cuebot.
/// </summary>
public sealed class ModelStore
{
    private const string LogSource = "Voice Control";
    private readonly HttpClient _http;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Task Task, CancellationTokenSource Cancel)> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelDownloadProgress> _progress = new(StringComparer.OrdinalIgnoreCase);

    public ModelStore(string directory, HttpClient http, AppLog log)
    {
        Directory = directory;
        _http = http;
        _log = log;
    }

    public string Directory { get; }

    /// <summary>Raised (on a background thread) when a download starts, progresses, finishes or a model is deleted.</summary>
    public event Action<string>? Changed;

    public string PathFor(WhisperModel model) => Path.Combine(Directory, model.FileName);

    /// <summary>The model file is present with the expected size (its hash was checked when it was downloaded).</summary>
    public bool IsDownloaded(WhisperModel model)
    {
        var file = new FileInfo(PathFor(model));
        return file.Exists && file.Length == model.SizeBytes;
    }

    public bool IsDownloading(WhisperModel model)
    {
        lock (_gate) return _downloads.ContainsKey(model.Id);
    }

    public ModelDownloadProgress? Progress(WhisperModel model)
    {
        lock (_gate) return _progress.GetValueOrDefault(model.Id);
    }

    /// <summary>Downloads the model unless it is already present. Concurrent calls share one download.</summary>
    public Task DownloadAsync(WhisperModel model, CancellationToken ct = default)
    {
        if (IsDownloaded(model)) return Task.CompletedTask;
        Task task;
        lock (_gate)
        {
            if (_downloads.TryGetValue(model.Id, out var running)) return running.Task.WaitAsync(ct);
            var cancel = new CancellationTokenSource();
            _progress[model.Id] = new ModelDownloadProgress(model.Id, 0, model.SizeBytes);
            task = Task.Run(() => DownloadCoreAsync(model, cancel.Token));
            _downloads[model.Id] = (task, cancel);
        }
        Changed?.Invoke(model.Id);
        _ = task.ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_downloads.Remove(model.Id, out var done)) done.Cancel.Dispose();
                _progress.Remove(model.Id);
            }
            Changed?.Invoke(model.Id);
        }, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    public void CancelDownload(WhisperModel model)
    {
        lock (_gate)
        {
            if (_downloads.TryGetValue(model.Id, out var running)) running.Cancel.Cancel();
        }
    }

    public void Delete(WhisperModel model)
    {
        CancelDownload(model);
        File.Delete(PathFor(model));
        Changed?.Invoke(model.Id);
    }

    private async Task DownloadCoreAsync(WhisperModel model, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var target = PathFor(model);
        var part = target + ".part";
        _log.Info(LogSource, $"Downloading Whisper model {model.DisplayName} ({model.SizeText}) from {model.Url.Host}");
        try
        {
            using var response = await _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The model download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var lastReport = 0L;
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
                    if (received > model.SizeBytes) throw new InvalidOperationException("The model download is larger than expected.");
                    if (received - lastReport < 512 * 1024 && received != model.SizeBytes) continue;
                    lastReport = received;
                    lock (_gate) _progress[model.Id] = new ModelDownloadProgress(model.Id, received, model.SizeBytes);
                    Changed?.Invoke(model.Id);
                }
            }
            if (received != model.SizeBytes)
                throw new InvalidOperationException($"The model download stopped early ({received} of {model.SizeBytes} bytes).");
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded model failed its checksum and was discarded. Try again.");
            File.Move(part, target, overwrite: true);
            _log.Info(LogSource, $"Whisper model {model.DisplayName} downloaded and verified");
        }
        catch (Exception ex)
        {
            TryDelete(part);
            if (ex is OperationCanceledException) _log.Info(LogSource, $"Download of {model.DisplayName} cancelled");
            else _log.Error(LogSource, $"Download of {model.DisplayName} failed", ex);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
