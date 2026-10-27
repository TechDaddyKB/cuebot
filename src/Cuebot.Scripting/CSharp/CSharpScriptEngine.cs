using System.Diagnostics;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Scripting.Compat;

namespace Cuebot.Scripting;

/// <summary>
/// Runs C# scripts in-process. Scripts are trusted user code: they run with the app's full permissions and are not
/// sandboxed. Each compiled script also stays loaded for the lifetime of the process. Code declaring a Streamer.bot
/// style <c>CPHInline</c> class is run through the CPH compatibility layer instead of as a script body.
/// </summary>
public sealed class CSharpScriptEngine(ScriptApi api, CphServices? cph = null) : IDisposable
{
    private readonly CSharpScriptCompiler _compiler = new();
    private readonly CphServices _cph = cph ?? new CphServices();
    private readonly CphInlineHost _inline = new(cph?.Log);

    /// <summary>Runs a script body. Returns false when the script returned <c>false</c> (stop the action).</summary>
    /// <param name="timeout">Maximum run time; null for no limit. On timeout the script's token is cancelled.</param>
    public async Task<bool> RunAsync(ActionRunContext context, string code, TimeSpan? timeout, CancellationToken ct)
    {
        // Compiling is not part of the time limit.
        if (CphInlineHost.IsInlineClass(code))
        {
            await Task.Run(() => _inline.Get(code), ct).ConfigureAwait(false);
            return await RunWithTimeoutAsync(
                token => _inline.ExecuteAsync(code, new CphProxy(_cph, api, context, token), context, token), timeout, ct)
                .ConfigureAwait(false);
        }

        var runner = await Task.Run(() => _compiler.GetRunner(code), ct).ConfigureAwait(false);
        return await RunWithTimeoutAsync(async token =>
        {
            var globals = new CSharpScriptGlobals(context.Args, new CuebotHelpers(api, context, token), token,
                new CphProxy(_cph, api, context, token));
            var result = await Task.Run(() => runner(globals, token), token).ConfigureAwait(false);
            return result is not false;
        }, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<bool> RunWithTimeoutAsync(Func<CancellationToken, Task<bool>> body, TimeSpan? timeout, CancellationToken ct)
    {
        using var scriptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = body(scriptCts.Token);

        try
        {
            return await run.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
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

    /// <summary>Calls Dispose() on Streamer.bot-style scripts.</summary>
    public void Dispose() => _inline.Dispose();
}
