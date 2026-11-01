using System.Diagnostics;
using System.Text;
using Cuebot.Core.Logging;

namespace Cuebot.Integrations.NowPlaying.Smtc;

/// <summary>A running helper: lines out, lines in (tests substitute a fake).</summary>
internal interface ILineProcess : IDisposable
{
    /// <summary>Output lines until the process ends (then null).</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Ends the process (and anything it started).</summary>
    void Kill();
}

/// <summary>A child process with redirected UTF-8 stdin/stdout and no window.</summary>
internal sealed class ChildLineProcess : ILineProcess
{
    private readonly Process _process;

    private ChildLineProcess(Process process) => _process = process;

    public static ILineProcess Start(string fileName, string arguments)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        process.ErrorDataReceived += (_, _) => { };   // drain stderr so the helper never blocks on it
        process.BeginErrorReadLine();
        return new ChildLineProcess(process);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) => await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        try { _process.StandardInput.Close(); }   // the helper exits on its own when stdin closes
        catch (IOException) { }
        catch (InvalidOperationException) { }
        Kill();
        _process.Dispose();
    }
}

/// <summary>
/// The Windows media sessions (System Media Transport Controls), read by a small Windows PowerShell 5.1 helper
/// (smtc-helper.ps1) because Cuebot is one .NET build without the Windows SDK projection. The helper prints the
/// session list as JSON lines when it changes and takes media keys on stdin. It is restarted when it stops (with a
/// growing pause) and killed when Cuebot stops; it also exits by itself when Cuebot's end of the pipe closes.
/// </summary>
internal sealed class SmtcSource : ISystemMediaSource
{
    private const string Category = "Now playing";

    private readonly AppLog _log;
    private readonly Func<ILineProcess> _start;
    private readonly TimeSpan _firstRetry;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ILineProcess? _process;
    private IReadOnlyList<PlayerSession> _sessions = [];
    private string? _currentId;
    private string _status = "Not watching music players yet.";
    private bool _unsupported;

    public SmtcSource(AppLog log, Func<ILineProcess>? start = null, TimeSpan? firstRetry = null)
    {
        _log = log;
        _start = start ?? StartPowerShell;
        _firstRetry = firstRetry ?? TimeSpan.FromSeconds(2);
    }

    public string Name => "Windows media controls";
    public bool IsSupported => !_unsupported;
    public bool IsRunning => _loop is { IsCompleted: false };
    public string StatusText { get { lock (_gate) return _status; } }
    public string? CurrentSessionId { get { lock (_gate) return _currentId; } }
    public IReadOnlyList<PlayerSession> Sessions { get { lock (_gate) return _sessions; } }

    public event Action? Changed;

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false } || _unsupported) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            _process?.Kill();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _sessions = [];
            _currentId = null;
            if (!_unsupported) _status = "Not watching music players.";
        }
        Changed?.Invoke();
    }

    public async Task<bool> ControlAsync(string sessionId, MediaCommand command, CancellationToken ct = default)
    {
        ILineProcess? process;
        lock (_gate)
        {
            process = _process;
            if (process is null || _sessions.All(s => s.Id != sessionId)) return false;
        }
        try
        {
            await process.WriteLineAsync(SmtcProtocol.Command(command, sessionId), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retry = _firstRetry;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                using var process = _start();
                lock (_gate) _process = process;
                SetStatus("Looking for music players…");
                while (await process.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (SmtcProtocol.Parse(line) is not { } message) continue;
                    if (Handle(message)) return;
                }
                if (!ct.IsCancellationRequested) _log.Warn(Category, "The Windows media helper stopped; starting it again.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn(Category, $"Could not run the Windows media helper: {ex.Message}");
                SetStatus("Cannot read the Windows media controls right now. Cuebot keeps trying.");
            }
            finally
            {
                lock (_gate)
                {
                    _process = null;
                    _sessions = [];
                    _currentId = null;
                }
                Changed?.Invoke();
            }
            // A helper that ran for a while gets a quick restart; one that keeps failing waits longer (up to a minute).
            retry = DateTime.UtcNow - started > TimeSpan.FromMinutes(1) ? _firstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            try { await Task.Delay(retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Applies a helper message; true when the helper said this Windows cannot do it (stop for good).</summary>
    internal bool Handle(SmtcMessage message)
    {
        if (message.IsUnsupported)
        {
            _log.Warn(Category, $"This Windows has no media controls Cuebot can read ({message.Message}).");
            lock (_gate) _unsupported = true;
            SetStatus("Cuebot cannot read music players on this version of Windows (it needs Windows 10 1809 or newer). Spotify below still works.");
            Changed?.Invoke();
            return true;
        }
        if (message.IsError)
        {
            _log.Debug(Category, $"Windows media helper: {message.Message}");
            return false;
        }
        if (!message.IsSessions) return false;
        lock (_gate)
        {
            _sessions = message.Sessions;
            _currentId = message.CurrentId.Length > 0 ? message.CurrentId : null;
        }
        var count = message.Sessions.Count;
        SetStatus(count switch
        {
            0 => "No music player is open. Apps that show in the Windows volume pop-up show up here.",
            1 => "Watching 1 music player.",
            _ => $"Watching {count} music players.",
        });
        Changed?.Invoke();
        return false;
    }

    private void SetStatus(string text)
    {
        lock (_gate) _status = text;
    }

    private static ILineProcess StartPowerShell()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return ChildLineProcess.Start(File.Exists(path) ? path : "powershell.exe", SmtcProtocol.Arguments(SmtcProtocol.Script()));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
