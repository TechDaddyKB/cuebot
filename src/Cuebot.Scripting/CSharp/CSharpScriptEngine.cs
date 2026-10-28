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
/// <param name="globalReferences">
/// Custom DLLs every C# code is compiled with (Streamer.bot's C# compiler references), as full paths; null for none.
/// </param>
public sealed class CSharpScriptEngine(ScriptApi api, CphServices? cph = null, Func<IReadOnlyList<string>>? globalReferences = null)
    : IDisposable
{
    private readonly CSharpScriptCompiler _compiler = new();
    private readonly CphServices _cph = cph ?? new CphServices();
    private readonly CphInlineHost _inline = new(cph?.Log, globalReferences);

    /// <summary>Runs a script body. Returns false when the script returned <c>false</c> (stop the action).</summary>
    /// <param name="timeout">Maximum run time; null for no limit. On timeout the script's token is cancelled.</param>
    /// <param name="references">Custom DLLs (full paths) this code needs, on top of the global references.</param>
    public async Task<bool> RunAsync(ActionRunContext context, string code, TimeSpan? timeout, CancellationToken ct,
        IReadOnlyList<string>? references = null)
    {
        // Compiling is not part of the time limit.
        if (CphInlineHost.IsInlineClass(code))
        {
            await Task.Run(() => _inline.Get(code, references), ct).ConfigureAwait(false);
            return await RunWithTimeoutAsync(
                token => _inline.ExecuteAsync(code, new CphProxy(_cph, api, context, token, _inline), context, token, references),
                timeout, ct).ConfigureAwait(false);
        }

        var all = Combine(globalReferences?.Invoke(), references);
        var runner = await Task.Run(() => _compiler.GetRunner(code, all), ct).ConfigureAwait(false);
        return await RunWithTimeoutAsync(async token =>
        {
            var globals = new CSharpScriptGlobals(context.Args, new CuebotHelpers(api, context, token), token,
                new CphProxy(_cph, api, context, token, _inline));
            var result = await Task.Run(() => runner(globals, token), token).ConfigureAwait(false);
            return result is not false;
        }, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Execute C# Method": runs the public parameterless method <paramref name="methodName"/> of the CPHInline class
    /// in the "Execute C# code" step named <paramref name="codeName"/> (see <see cref="InlineCodeFinder"/>), on that
    /// code's long-lived instance, with this run's arguments. Returns the method's bool result (true for void).
    /// Throws <see cref="ScriptException"/> when the code or the method does not exist.
    /// </summary>
    public async Task<bool> RunMethodAsync(ActionRunContext context, string codeName, string methodName, TimeSpan? timeout,
        CancellationToken ct)
    {
        var profile = _cph.Profile ?? throw new ScriptException("Execute C# Method: the action list is not available");
        var step = InlineCodeFinder.Find(profile.Snapshot.Actions, codeName)
                   ?? throw new ScriptException($"Execute C# Method: no C# code (CPHInline class) is named '{codeName.Trim()}'");
        var code = step.Parameters["code"];
        var references = CSharpCodeSubAction.References(step);
        await Task.Run(() => _inline.Get(code, references), ct).ConfigureAwait(false);
        return await RunWithTimeoutAsync(token => Task.Run(() =>
                _inline.InvokeMethod(code, methodName.Trim(), new CphProxy(_cph, api, context, token, _inline), context.Args, references)
                ?? throw new ScriptException($"Execute C# Method: '{codeName.Trim()}' has no public method {methodName.Trim()}() without parameters"),
            token), timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles C# code ahead of its first run ("Precompile on Application Start"). For CPHInline classes this also
    /// runs Init(), with a CPH that has no action run (as Streamer.bot does at startup). Throws
    /// <see cref="ScriptException"/> on compile errors.
    /// </summary>
    public void Precompile(string code, IReadOnlyList<string>? references = null)
    {
        if (CphInlineHost.IsInlineClass(code))
        {
            _inline.Precompile(code, new CphProxy(_cph, api, null, CancellationToken.None, _inline), references);
            return;
        }
        _compiler.GetRunner(code, Combine(globalReferences?.Invoke(), references));
    }

    private static IReadOnlyList<string> Combine(IReadOnlyList<string>? first, IReadOnlyList<string>? second) =>
        (first?.Count ?? 0) == 0 ? second ?? [] : (second?.Count ?? 0) == 0 ? first! : [.. first!.Union(second!, StringComparer.OrdinalIgnoreCase)];

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
