using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// One persistent Python process running cuebot_worker.py. Sends runs, answers the script's host calls through
/// <see cref="ScriptApi"/>, forwards print output to the action's log, and fails pending runs if the process dies.
/// </summary>
internal sealed class PythonWorker : IAsyncDisposable
{
    private const string Source = "Python";
    private const int StderrTailLines = 10;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly AsyncLocal<bool> InHostCall = new();

    private readonly Process _process;
    private readonly ScriptApi _api;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRun> _runs = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private long _nextRunId;
    private volatile bool _exited;

    private sealed record PendingRun(ActionRunContext Context, TaskCompletionSource<PythonRunOutcome> Completion, CancellationToken Token);

    private PythonWorker(string executable, string workerScript, string workingDirectory, ScriptApi api, AppLog log)
    {
        Executable = executable;
        _api = api;
        _log = log;
        var startInfo = ProcessRunner.CreateStartInfo(executable, ["-u", workerScript], workingDirectory);
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}");
        _process.StandardInput.NewLine = "\n";
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    /// <summary>True while the current async flow is executing a host call made by a Python script.</summary>
    public static bool IsHandlingHostCall => InHostCall.Value;

    public string Executable { get; }

    public bool HasExited => _exited;

    /// <summary>Starts the process and waits for the worker's "ready" message.</summary>
    public static async Task<PythonWorker> StartAsync(string executable, string workerScript, string workingDirectory,
        ScriptApi api, AppLog log, CancellationToken ct)
    {
        var worker = new PythonWorker(executable, workerScript, workingDirectory, api, log);
        try
        {
            await worker._ready.Task.WaitAsync(StartupTimeout, ct).ConfigureAwait(false);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs a script. Cancelling the token abandons the run; the caller should then dispose the worker.</summary>
    public async Task<PythonRunOutcome> RunAsync(ActionRunContext context, string code, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRunId).ToString();
        var run = new PendingRun(context, new(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        _runs[id] = run;
        try
        {
            await using var cancel = ct.Register(() => run.Completion.TrySetCanceled(ct));
            await WriteAsync(PythonProtocol.Run(id, code, context.Args), ct).ConfigureAwait(false);
            if (_exited) run.Completion.TrySetException(ExitedError());
            return await run.Completion.Task.ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw ExitedError();
        }
        finally
        {
            _runs.TryRemove(id, out _);
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) HandleMessage(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed; handled below.
        }
        finally
        {
            _exited = true;
            var error = ExitedError();
            _ready.TrySetException(error);
            foreach (var run in _runs.Values) run.Completion.TrySetException(error);
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    if (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
                }
                _log.Warn(Source, line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed with the process.
        }
    }

    private void HandleMessage(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            _log.Warn(Source, $"Unexpected worker output: {line}");
            return;
        }

        using (document)
        {
            var message = document.RootElement;
            var run = _runs.GetValueOrDefault(message.GetString("id"));
            switch (message.GetString("type"))
            {
                case "ready":
                    _ready.TrySetResult();
                    break;
                case "print":
                    if (run is null) _log.Info(Source, message.GetString("text"));
                    else run.Context.Log.Info(run.Context.Action.Name, message.GetString("text"));
                    break;
                case "call":
                    var callId = message.GetString("callId");
                    var name = message.GetString("name");
                    var parameters = message.GetElement("params")?.Clone() ?? default;
                    _ = Task.Run(() => HandleCallAsync(run, callId, name, parameters));
                    break;
                case "done":
                    run?.Completion.TrySetResult(new(true, message.GetElement("result")?.Clone() ?? default, "", ""));
                    break;
                case "error":
                    run?.Completion.TrySetResult(new(false, default, message.GetString("error"), message.GetString("traceback")));
                    break;
            }
        }
    }

    private async Task HandleCallAsync(PendingRun? run, string callId, string name, JsonElement parameters)
    {
        InHostCall.Value = true;
        string reply;
        try
        {
            if (run is null) throw new ScriptException("No script is running");
            var result = await _api.CallAsync(run.Context, name, parameters, run.Token).ConfigureAwait(false);
            reply = PythonProtocol.CallResult(callId, result);
        }
        catch (Exception ex)
        {
            reply = PythonProtocol.CallError(callId, ex.Message);
        }

        try
        {
            await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The worker exited while the call ran; its run has already failed.
        }
    }

    private async Task WriteAsync(string line, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private InvalidOperationException ExitedError()
    {
        var exitCode = _process.WaitForExit(1000) ? $" (exit code {_process.ExitCode})" : "";
        string tail;
        lock (_stderrTail) tail = string.Join(Environment.NewLine, _stderrTail);
        return new InvalidOperationException(
            $"The Python worker stopped unexpectedly{exitCode}" + (tail.Length > 0 ? $": {tail}" : ""));
    }

    public async ValueTask DisposeAsync()
    {
        ProcessRunner.KillQuietly(_process);
        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump, _process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Warn(Source, "The Python worker did not shut down cleanly");
        }
        _process.Dispose();
        _writeGate.Dispose();
    }
}
