using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Scripting;

/// <summary>
/// A host function callable from user scripts (C# and Python share this surface).
/// <paramref name="Invoke"/> receives the JSON object of named arguments the script passed and returns a
/// JSON-serializable result.
/// </summary>
public sealed record ScriptFunction(
    string Name,
    string Description,
    IReadOnlyList<string> ParameterNames,
    Func<ActionRunContext, JsonElement, CancellationToken, Task<object?>> Invoke);

/// <summary>Modules implement this (registered in DI) to expose functions to scripts.</summary>
public interface IScriptFunctionProvider
{
    IEnumerable<ScriptFunction> GetFunctions();
}

public sealed class ScriptException(string message) : Exception(message);

public sealed class ScriptApi
{
    private readonly Dictionary<string, ScriptFunction> _functions;

    public ScriptApi(IEnumerable<IScriptFunctionProvider> providers)
    {
        _functions = providers.SelectMany(p => p.GetFunctions()).ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        Functions = _functions.Values.OrderBy(f => f.Name).ToArray();
    }

    public IReadOnlyList<ScriptFunction> Functions { get; }

    public Task<object?> CallAsync(ActionRunContext context, string name, JsonElement args, CancellationToken ct)
    {
        if (!_functions.TryGetValue(name, out var function)) throw new ScriptException($"Unknown function '{name}'");
        return function.Invoke(context, args, ct);
    }

    public Task<object?> CallAsync(ActionRunContext context, string name, object? args, CancellationToken ct) =>
        CallAsync(context, name, JsonSerializer.SerializeToElement(args ?? new { }), ct);
}

public static class ScriptArgs
{
    public static string GetString(this JsonElement args, string name, string fallback = "") =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : v.ToString()
            : fallback;

    public static bool GetBool(this JsonElement args, string name, bool fallback = false) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
                _ => fallback,
            }
            : fallback;

    public static JsonElement? GetElement(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? v : null;

    /// <summary>Converts a JSON value to a plain CLR value for storing in action arguments.</summary>
    public static object? ToClr(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
