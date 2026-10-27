using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cuebot.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Cuebot.Scripting;

/// <summary>Compiles C# script bodies with Roslyn and caches the compiled delegates by code hash.</summary>
internal sealed class CSharpScriptCompiler
{
    /// <summary>Virtual file name of every script; stack frames with this name are script lines.</summary>
    public const string ScriptFileName = "script.csx";

    private const int MaxReportedErrors = 5;

    private static readonly string[] Imports =
    [
        "System", "System.Linq", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks",
        "System.Text.Json", "System.Net.Http",
    ];

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(GetReferences())
        .WithImports(Imports)
        .WithFilePath(ScriptFileName)
        .WithFileEncoding(Encoding.UTF8)
        .WithEmitDebugInformation(true);

    private readonly BoundedCache<string, ScriptRunner<object>> _cache;

    public CSharpScriptCompiler(int capacity = 64) => _cache = new(capacity);

    public int CachedCount => _cache.Count;

    /// <summary>Returns a runnable delegate for the code. Throws <see cref="ScriptException"/> on compile errors.</summary>
    public ScriptRunner<object> GetRunner(string code)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (_cache.TryGet(key, out var cached)) return cached;

        var script = CSharpScript.Create<object>(code, Options, typeof(CSharpScriptGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new ScriptException(FormatErrors(errors));

        var runner = script.CreateDelegate();
        _cache.Set(key, runner);
        return runner;
    }

    internal static string FormatErrors(IReadOnlyList<Diagnostic> errors)
    {
        var lines = errors.Take(MaxReportedErrors).Select(FormatDiagnostic).ToList();
        if (errors.Count > MaxReportedErrors) lines.Add($"...and {errors.Count - MaxReportedErrors} more");
        return "C# compile error at " + string.Join(Environment.NewLine, lines);
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"line {position.Line + 1}, column {position.Character + 1}: " +
               $"{diagnostic.Id} {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";
    }

    private static IEnumerable<Assembly> GetReferences() =>
    [
        typeof(object).Assembly,
        Assembly.Load("System.Runtime"),
        Assembly.Load("System.Collections"),
        typeof(Enumerable).Assembly,
        typeof(System.Text.RegularExpressions.Regex).Assembly,
        typeof(System.Text.Json.JsonElement).Assembly,
        typeof(HttpClient).Assembly,
        typeof(Uri).Assembly,
        typeof(Console).Assembly,
        typeof(CSharpScriptGlobals).Assembly,
        typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
        typeof(Cuebot.Core.Actions.ActionEngine).Assembly,
    ];
}
