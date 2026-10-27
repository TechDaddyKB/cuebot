using System.Diagnostics;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// Runs C# scripts in-process. Scripts are trusted user code: they run with the app's full permissions and are not
/// sandboxed. Each compiled script also stays loaded for the lifetime of the process.
/// </summary>
public sealed class CSharpScriptEngine(ScriptApi api)
{
    private readonly CSharpScriptCompiler _compiler = new();

    /// <summary>Runs a script body. Returns false when the script returned <c>false</c> (stop the action).</summary>
    /// <param name="timeout">Maximum run time; null for no limit. On timeout the script's token is cancelled.</param>
    public async Task<bool> RunAsync(ActionRunContext context, string code, TimeSpan? timeout, CancellationToken ct)
    {
        var runner = await Task.Run(() => _compiler.GetRunner(code), ct).ConfigureAwait(false);
        using var scriptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var globals = new CSharpScriptGlobals(context.Args, new CuebotHelpers(api, context, scriptCts.Token), scriptCts.Token);
        var run = Task.Run(() => runner(globals, scriptCts.Token), scriptCts.Token);

        try
        {
            var result = await run.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return result is not false;
        }
        catch (TimeoutException) when (!run.IsCompleted)
        {
            scriptCts.Cancel();
            ObserveLateFailure(run);
            throw new TimeoutException($"The C# script did not finish within {timeout!.Value.TotalSeconds:0.##} seconds");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ObserveLateFailure(run);
            throw;
        }
        catch (Exception ex) when (ex is not ScriptException)
        {
            throw new ScriptException(DescribeRuntimeError(ex));
        }
    }

    /// <summary>Formats a script exception as "line N: Type: message" using the script's debug information.</summary>
    internal static string DescribeRuntimeError(Exception ex)
    {
        var line = new StackTrace(ex, fNeedFileInfo: true).GetFrames()
            .FirstOrDefault(f => Path.GetFileName(f.GetFileName()) == CSharpScriptCompiler.ScriptFileName)?
            .GetFileLineNumber();
        var where = line is > 0 ? $"line {line}: " : "";
        return $"C# error at {where}{ex.GetType().Name}: {ex.Message}";
    }

    private static void ObserveLateFailure(Task task) =>
        task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
}
