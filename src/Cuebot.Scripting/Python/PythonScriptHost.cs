using System.Reflection;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// Runs Python scripts in a persistent worker process, one at a time. The worker starts on first use, restarts
/// when it crashes or the interpreter changes, is stopped before packages or the runtime are modified, and is
/// killed (then restarted on the next run) when a script times out. A script's host call may run an action
/// containing another Python script; that nested run reuses the worker.
/// </summary>
public sealed class PythonScriptHost : IAsyncDisposable
{
    private const string WorkerResource = "Cuebot.Scripting.cuebot_worker.py";
    private static readonly Lazy<string> WorkerSource = new(ReadWorkerSource);

    private readonly PythonRuntime _runtime;
    private readonly ScriptApi _api;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PythonWorker? _worker;

    public PythonScriptHost(PythonRuntime runtime, ScriptApi api, AppLog log)
    {
        _runtime = runtime;
        _api = api;
        _log = log;
        runtime.ReleaseInterpreter = StopWorkerWhenIdleAsync;
    }

    /// <summary>Runs a script body. Returns false when the script returned <c>False</c> (stop the action).</summary>
    /// <param name="timeout">Maximum run time; null for no limit. On timeout the worker process is restarted.</param>
    public async Task<bool> RunAsync(ActionRunContext context, string code, TimeSpan? timeout, CancellationToken ct)
    {
        if (PythonWorker.IsHandlingHostCall && _worker is { HasExited: false } current)
            return await RunOnAsync(current, context, code, timeout, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var worker = await GetWorkerAsync(ct).ConfigureAwait(false);
            return await RunOnAsync(worker, context, code, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> RunOnAsync(PythonWorker worker, ActionRunContext context, string code, TimeSpan? timeout,
        CancellationToken ct)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit) runCts.CancelAfter(limit);

        PythonRunOutcome outcome;
        try
        {
            outcome = await worker.RunAsync(context, code, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopWorkerAsync(worker).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(
                $"The Python script did not finish within {timeout!.Value.TotalSeconds:0.##} seconds; the Python worker was restarted");
        }

        if (outcome.Succeeded) return outcome.ShouldContinue;
        if (outcome.Traceback.Length > 0) context.Log.Error(context.Action.Name, outcome.Traceback.TrimEnd());
        throw new ScriptException($"Python error: {outcome.Error}");
    }

    /// <summary>The interpreter scripts run with.</summary>
    public PythonRuntime Runtime => _runtime;

    private async Task<PythonWorker> GetWorkerAsync(CancellationToken ct)
    {
        if (_runtime.PythonExecutable is null && _runtime.ShouldInstallAutomatically)
        {
            // First Python step on this computer: install now, skip this run (the download takes a while).
            _ = _runtime.InstallInBackground();
            throw new ScriptException(
                "Python is being installed for the first Python step (a one-time download); this step was skipped and runs normally once it finishes. Progress is on Automation > Scripting");
        }
        var executable = _runtime.PythonExecutable ?? throw new ScriptException(_runtime.NotReadyMessage());
        if (_worker is { HasExited: false } current && current.Executable == executable)
            return current;

        if (_worker is not null) await StopWorkerAsync(_worker).ConfigureAwait(false);
        await File.WriteAllTextAsync(_runtime.Layout.WorkerScript, WorkerSource.Value, ct).ConfigureAwait(false);
        _worker = await PythonWorker.StartAsync(executable, _runtime.Layout.WorkerScript, _runtime.Layout.Root, _api, _log, ct)
            .ConfigureAwait(false);
        return _worker;
    }

    /// <summary>Waits for the current script to finish, then stops the worker (before Python files change).</summary>
    private async Task StopWorkerWhenIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker is { } worker) await StopWorkerAsync(worker).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopWorkerAsync(PythonWorker worker)
    {
        Interlocked.CompareExchange(ref _worker, null, worker);
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private static string ReadWorkerSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(WorkerResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {WorkerResource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (_worker is { } worker) await StopWorkerAsync(worker).ConfigureAwait(false);
        _gate.Dispose();
    }
}
