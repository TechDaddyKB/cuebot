using System.Text.Json;

namespace Cuebot.Scripting;

/// <summary>Builds host-to-worker messages of the line-delimited JSON protocol (see cuebot_worker.py).</summary>
internal static class PythonProtocol
{
    public static string Run(string id, string code, IReadOnlyDictionary<string, object?> args) =>
        Serialize(new { type = "run", id, code, args = ToJsonSafe(args) });

    public static string CallResult(string callId, object? result) =>
        Serialize(new { type = "callResult", callId, result });

    public static string CallError(string callId, string error) =>
        Serialize(new { type = "callError", callId, error });

    /// <summary>Action arguments should be strings, numbers or bools; anything else is sent as its text.</summary>
    private static Dictionary<string, object?> ToJsonSafe(IReadOnlyDictionary<string, object?> args) =>
        args.ToDictionary(a => a.Key, a => a.Value switch
        {
            null or string or bool or int or long or double or float or decimal or JsonElement => a.Value,
            _ => (object?)a.Value.ToString(),
        });

    private static string Serialize(object message) => JsonSerializer.Serialize(message);
}

/// <summary>How a Python run ended: completed with a result, or failed with an error and traceback.</summary>
internal sealed record PythonRunOutcome(bool Succeeded, JsonElement Result, string Error, string Traceback)
{
    /// <summary>False only when the script returned <c>False</c>.</summary>
    public bool ShouldContinue => Result.ValueKind != JsonValueKind.False;
}
