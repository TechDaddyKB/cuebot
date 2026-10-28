using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Streamer.bot.Plugin.Interface;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Runs Streamer.bot-shaped scripts unchanged: a <c>CPHInline</c> class with <c>bool Execute()</c> and optional
/// <c>Init()</c> / <c>Dispose()</c>. The class is compiled as a normal C# library whose <c>CPHInline</c> is made to
/// derive from <see cref="CPHInlineBase"/> (which supplies <c>CPH</c> and <c>args</c>). One instance per distinct
/// code lives for the app's lifetime, like Streamer.bot's: fields persist between runs, <c>Init()</c> runs once before
/// the first run and <c>Dispose()</c> at shutdown.
/// </summary>
internal sealed partial class CphInlineHost : IDisposable
{
    private const int Capacity = 64;
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    private static readonly string GlobalUsings =
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\n"
        // Streamer.bot's documented examples use its data classes (TwitchUserInfo, ...), Platform and EventType with
        // only `using System;`.
        + "global using Streamer.bot.Plugin.Interface.Model;\nglobal using Streamer.bot.Plugin.Interface.Enums;\n"
        + "global using Streamer.bot.Common.Events;\nglobal using Twitch.Common.Models.Api;\n";

    private readonly Dictionary<string, Compiled> _compiled = [];
    private readonly LinkedList<string> _order = new();
    private readonly object _gate = new();
    private readonly AppLog? _log;
    private readonly Func<IReadOnlyList<string>>? _globalReferences;
    private bool _disposed;

    /// <param name="globalReferences">Custom DLLs (full paths) every class is compiled with; null for none.</param>
    public CphInlineHost(AppLog? log = null, Func<IReadOnlyList<string>>? globalReferences = null)
    {
        _log = log;
        _globalReferences = globalReferences;
    }

    [GeneratedRegex(@"\bclass\s+CPHInline\b")]
    private static partial Regex InlineClass();

    /// <summary>True for Streamer.bot-style code (declares a <c>CPHInline</c> class).</summary>
    public static bool IsInlineClass(string code) => InlineClass().IsMatch(code);

    public int CompiledCount
    {
        get { lock (_gate) return _compiled.Count; }
    }

    /// <summary>Runs Execute() with this run's CPH and args. Returns its result (false stops the action).</summary>
    /// <param name="references">Custom DLLs the code needs (full paths), besides the framework and Cuebot's.</param>
    public Task<bool> ExecuteAsync(string code, CphProxy proxy, ActionRunContext context, CancellationToken ct,
        IReadOnlyList<string>? references = null) =>
        Task.Run(() =>
        {
            var compiled = Get(code, references);
            CPHInlineBase.Enter(proxy, context.Args);
            compiled.EnsureInitialized();
            return compiled.Execute();
        }, ct);

    /// <summary>
    /// Runs the public parameterless method <paramref name="methodName"/> of the code's CPHInline instance (the
    /// same long-lived instance its Execute() uses), with the caller's CPH and args, for <c>CPH.ExecuteMethod</c>.
    /// Returns the method's bool result (true for void methods), or null when there is no such method.
    /// </summary>
    internal bool? InvokeMethod(string code, string methodName, CphProxy proxy, Dictionary<string, object?> args,
        IReadOnlyList<string>? references = null)
    {
        var compiled = Get(code, references);
        CPHInlineBase.Enter(proxy, args);
        compiled.EnsureInitialized();
        return compiled.Invoke(methodName);
    }

    /// <summary>
    /// Compiles the code and runs its Init() now ("Precompile on Application Start"), with a CPH that has no action
    /// run. Later runs reuse the compiled instance and do not run Init() again.
    /// </summary>
    internal void Precompile(string code, CphProxy proxy, IReadOnlyList<string>? references = null)
    {
        var compiled = Get(code, references);
        CPHInlineBase.Enter(proxy, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
        compiled.EnsureInitialized();
    }

    /// <summary>Compiles (or returns the cached) script. Throws <see cref="ScriptException"/> with the compile errors.</summary>
    internal Compiled Get(string code, IReadOnlyList<string>? references = null)
    {
        var global = _globalReferences?.Invoke() ?? [];
        references = global.Count == 0 ? references ?? [] : [.. global.Union(references ?? [], StringComparer.OrdinalIgnoreCase)];
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code + CSharpReferences.Key(references))));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_compiled.TryGetValue(key, out var cached)) return cached;
        }

        var compiled = Compile(code, references);
        Compiled? evicted = null;
        lock (_gate)
        {
            if (_compiled.TryGetValue(key, out var raced)) return raced;
            _compiled[key] = compiled;
            _order.AddFirst(key);
            if (_order.Count > Capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _compiled.Remove(oldest, out evicted);
            }
        }
        evicted?.Dispose(_log);
        return compiled;
    }

    private static Compiled Compile(string code, IReadOnlyList<string> customReferences)
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var tree = CSharpSyntaxTree.ParseText(code, parse, CSharpScriptCompiler.ScriptFileName, Encoding.UTF8);
        tree = tree.WithRootAndOptions(AddBaseClass((CompilationUnitSyntax)tree.GetRoot()), parse);
        var usings = CSharpSyntaxTree.ParseText(GlobalUsings, parse, "usings.cs", Encoding.UTF8);

        var compilation = CSharpCompilation.Create(
            "CPHInline_" + Guid.NewGuid().ToString("N"),
            [tree, usings],
            WithCustom(References.Value, customReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (!result.Success) throw new ScriptException(CSharpScriptCompiler.FormatErrors(errors));

        pe.Position = 0;
        pdb.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(pe, pdb);
        var type = assembly.GetTypes().FirstOrDefault(t => t.Name == "CPHInline")
                   ?? throw new ScriptException("C# compile error: no CPHInline class found");
        return new Compiled(type);
    }

    /// <summary>Makes every <c>CPHInline</c> class derive from <see cref="CPHInlineBase"/>, keeping line numbers.</summary>
    private static CompilationUnitSyntax AddBaseClass(CompilationUnitSyntax root)
    {
        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("global::Streamer.bot.Plugin.Interface.CPHInlineBase"));
        return root.ReplaceNodes(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText == "CPHInline"),
            (_, cls) =>
            {
                if (cls.BaseList is { } list)
                {
                    if (list.Types.Any(t => t.Type.ToString().EndsWith("CPHInlineBase", StringComparison.Ordinal))) return cls;
                    return cls.WithBaseList(list.WithTypes(list.Types.Insert(0, baseType)));
                }
                var trailing = cls.Identifier.TrailingTrivia;
                var newList = SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(baseType))
                    .WithColonToken(SyntaxFactory.Token(SyntaxKind.ColonToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithTrailingTrivia(trailing);
                return cls.WithIdentifier(cls.Identifier.WithTrailingTrivia()).WithBaseList(newList);
            });
    }

    /// <summary>The standard references plus the user's DLLs, skipping any the standard set already has.</summary>
    private static IReadOnlyList<MetadataReference> WithCustom(IReadOnlyList<MetadataReference> standard, IReadOnlyList<string> custom)
    {
        if (custom.Count == 0) return standard;
        var present = standard.Select(r => Path.GetFileName(r.Display ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. standard, .. CSharpReferences.Load(custom).Where(r => !present.Contains(Path.GetFileName(r.Display ?? "")))];
    }

    /// <summary>
    /// Every framework assembly plus Cuebot's and Newtonsoft.Json (which Streamer.bot ships), so scripts can use any
    /// BCL namespace like in Streamer.bot.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) paths.Add(path);
        }
        foreach (var assembly in new[] { typeof(object).Assembly, typeof(CphProxy).Assembly, typeof(ActionEngine).Assembly,
                     typeof(Newtonsoft.Json.JsonConvert).Assembly })
        {
            if (!string.IsNullOrEmpty(assembly.Location)) paths.Add(assembly.Location);
        }

        var references = new List<MetadataReference>();
        foreach (var path in paths)
        {
            // Native libraries and other non-.NET files in the list are skipped.
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !IsManaged(path)) continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }
        return references;
    }

    private static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Compiled[] all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = [.. _compiled.Values];
            _compiled.Clear();
            _order.Clear();
        }
        foreach (var compiled in all) compiled.Dispose(_log);
    }

    /// <summary>One compiled CPHInline class and its long-lived instance.</summary>
    internal sealed class Compiled
    {
        private readonly object _instance;
        private readonly Func<bool> _execute;
        private readonly Action? _init;
        private readonly Action? _dispose;
        private readonly object _initGate = new();
        private bool _initialized;

        public Compiled(Type type)
        {
            if (!typeof(CPHInlineBase).IsAssignableFrom(type))
                throw new ScriptException("C# compile error: CPHInline must not derive from another class");
            _instance = Activator.CreateInstance(type)
                        ?? throw new ScriptException("C# error: could not create CPHInline");
            var execute = FindMethod(type, "Execute")
                          ?? throw new ScriptException("C# compile error: CPHInline needs a public bool Execute() method");
            _execute = execute.ReturnType == typeof(bool)
                ? execute.CreateDelegate<Func<bool>>(_instance)
                : WrapVoid(execute.CreateDelegate<Action>(_instance));
            _init = ToAction(FindMethod(type, "Init"), _instance);
            _dispose = ToAction(FindMethod(type, "Dispose"), _instance);
        }

        public bool Execute() => _execute();

        /// <summary>Calls a public parameterless instance method by name (exact case first). Null when there is none.</summary>
        public bool? Invoke(string methodName)
        {
            var type = _instance.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var method = type.GetMethod(methodName, flags, Type.EmptyTypes)
                         ?? type.GetMethod(methodName, flags | BindingFlags.IgnoreCase, Type.EmptyTypes);
            if (method is null || method.IsSpecialName || method.DeclaringType == typeof(object)) return null;
            object? result;
            try
            {
                result = method.Invoke(_instance, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            return result is not false;
        }

        /// <summary>Runs Init() once, on the first run (with that run's CPH and args available).</summary>
        public void EnsureInitialized()
        {
            if (_init is null || Volatile.Read(ref _initialized)) return;
            lock (_initGate)
            {
                if (_initialized) return;
                _initialized = true;
                _init();
            }
        }

        public void Dispose(AppLog? log)
        {
            try
            {
                _dispose?.Invoke();
            }
            catch (Exception ex)
            {
                log?.Error("C#", "A CPHInline Dispose() failed", ex);
            }
        }

        private static MethodInfo? FindMethod(Type type, string name)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return method is not null && (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool)) ? method : null;
        }

        private static Action? ToAction(MethodInfo? method, object instance)
        {
            if (method is null) return null;
            if (method.ReturnType == typeof(void)) return method.CreateDelegate<Action>(instance);
            var func = method.CreateDelegate<Func<bool>>(instance);
            return () => func();
        }

        private static Func<bool> WrapVoid(Action action) => () =>
        {
            action();
            return true;
        };
    }
}
