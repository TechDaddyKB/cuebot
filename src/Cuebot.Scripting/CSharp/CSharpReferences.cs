using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Cuebot.Core.Scripting;
using Microsoft.CodeAnalysis;

namespace Cuebot.Scripting;

/// <summary>
/// Custom DLL references of C# code (Streamer.bot's per-code "References" list and its global C# compiler
/// references). Only files the user listed are used: each path must be an absolute path to an existing .NET
/// assembly. Nothing is downloaded or searched for. A referenced assembly is loaded from that exact path when the
/// compiled script first needs it.
/// </summary>
internal static class CSharpReferences
{
    /// <summary>Simple assembly name → the user-listed file it loads from.</summary>
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, MetadataReference> Metadata = new(StringComparer.OrdinalIgnoreCase);
    private static int _resolverInstalled;

    /// <summary>Splits a references parameter: one path per line (blank lines and # comments are skipped).</summary>
    public static IReadOnlyList<string> Parse(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Trim('"'))
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>A stable text for cache keys: the same list always gives the same key.</summary>
    public static string Key(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "" : "\n//references:" + string.Join("|", paths.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Metadata references for <paramref name="paths"/>, registering each assembly for loading at run time.
    /// Throws <see cref="ScriptException"/> naming the first path that is not usable.
    /// </summary>
    public static IReadOnlyList<MetadataReference> Load(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        InstallResolver();
        var references = new List<MetadataReference>(paths.Count);
        foreach (var raw in paths)
        {
            if (!Path.IsPathFullyQualified(raw))
                throw new ScriptException($"C# compile error: the reference '{raw}' must be a full path to a .dll file");
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path)) throw new ScriptException($"C# compile error: the reference '{path}' was not found");

            AssemblyName name;
            try
            {
                name = AssemblyName.GetAssemblyName(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
            {
                throw new ScriptException($"C# compile error: the reference '{path}' is not a .NET assembly");
            }
            if (name.Name is { Length: > 0 } simple) Known[simple] = path;
            references.Add(Metadata.GetOrAdd(path, p => MetadataReference.CreateFromFile(p)));
        }
        return references;
    }

    private static void InstallResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 1) return;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            name.Name is { } simple && Known.TryGetValue(simple, out var path) && File.Exists(path)
                ? context.LoadFromAssemblyPath(path)
                : null;
    }
}
