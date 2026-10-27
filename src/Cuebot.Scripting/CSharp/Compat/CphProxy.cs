using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Streamer.bot.Common.Events;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// The <c>CPH</c> object of C# scripts: Streamer.bot's method names mapped onto Cuebot. Methods are synchronous like
/// Streamer.bot's. Methods Cuebot cannot provide throw <see cref="NotSupportedException"/> naming the method.
/// See docs/SERVERS.md for the coverage table.
/// </summary>
public sealed partial class CphProxy
{
    private readonly CphServices _services;
    private readonly ScriptApi _api;
    private readonly ActionRunContext? _context;
    private readonly CancellationToken _ct;
    private readonly CphInlineHost? _inline;

    /// <param name="context">The action run; null while a script's Init() runs outside any action.</param>
    /// <param name="inline">The hosted CPHInline classes, for <see cref="ExecuteMethod"/>; null where there are none.</param>
    internal CphProxy(CphServices services, ScriptApi api, ActionRunContext? context, CancellationToken ct, CphInlineHost? inline = null)
    {
        _inline = inline;
        _services = services;
        _api = api;
        _context = context;
        _ct = ct;
    }

    private string ScriptName => _context?.Action.Name ?? "C# script";

    // ---- Arguments ------------------------------------------------------------------------------------------------

    /// <summary>Reads an argument converted to <typeparamref name="T"/>; false when missing or not convertible.</summary>
    public bool TryGetArg<T>(string argName, out T value)
    {
        value = default!;
        if (_context is null || !_context.Args.TryGetValue(argName, out var raw)) return false;
        if (!CphConvert.TryTo(raw, out T converted)) return false;
        value = converted;
        return true;
    }

    public bool TryGetArg(string argName, out object? value)
    {
        value = null;
        return _context is not null && _context.Args.TryGetValue(argName, out value);
    }

    /// <summary>Sets an argument for the rest of the action.</summary>
    public void SetArgument(string variableName, object? value) => RequireContext().Args[variableName] = value;

    /// <summary>The Streamer.bot source of the event that started the run (None for manual runs).</summary>
    public EventSource GetSource() =>
        CurrentEvent() is { } name && Enum.TryParse<EventSource>(name.Source, true, out var source) ? source : EventSource.None;

    /// <summary>The Streamer.bot event type that started the run (None when unknown or manual).</summary>
    public EventType GetEventType() =>
        CurrentEvent() is { } name && Enum.TryParse<EventType>(name.Source + name.Type, true, out var type) ? type : EventType.None;

    private StreamerBotEventName? CurrentEvent() =>
        _context?.Args.GetValueOrDefault("eventType") is string type && type.Length > 0
            ? StreamerBotEventMap.Map(type, _context.Args)
            : null;

    // ---- Logging and misc -----------------------------------------------------------------------------------------

    public void LogInfo(string message) => Log(LogLevel.Info, message);
    public void LogWarn(string message) => Log(LogLevel.Warning, message);
    public void LogError(string message) => Log(LogLevel.Error, message);
    public void LogDebug(string message) => Log(LogLevel.Debug, message);
    public void LogVerbose(string message) => Log(LogLevel.Debug, message);

    private void Log(LogLevel level, string message) =>
        (_context?.Log ?? _services.Log)?.Write(level, ScriptName, message ?? "");

    /// <summary>Pauses the script; honours the action's cancellation and the script timeout.</summary>
    public void Wait(int milliseconds) => Task.Delay(Math.Max(0, milliseconds), _ct).GetAwaiter().GetResult();

    /// <summary>A random integer between <paramref name="min"/> and <paramref name="max"/>, both included.</summary>
    public int Between(int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        return max == int.MaxValue ? Random.Shared.Next(min, max) : Random.Shared.Next(min, max + 1);
    }

    public double NextDouble() => Random.Shared.NextDouble();

    /// <summary>Cuebot's version (Streamer.bot returns its own).</summary>
    public string GetVersion() =>
        typeof(CphProxy).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private ActionRunContext RequireContext([CallerMemberName] string method = "") =>
        _context ?? throw new NotSupportedException($"CPH.{method} can only be used while an action runs, not from Init()");

    private static T Need<T>(T? service, [CallerMemberName] string method = "") where T : class =>
        service ?? throw new NotSupportedException($"CPH.{method} is not available: the Cuebot service it needs is not loaded");

    private static NotSupportedException Unsupported(string? reason = null, [CallerMemberName] string method = "") =>
        new($"CPH.{method} is not supported by Cuebot" + (reason is null ? "" : $" ({reason})"));

    /// <summary>Calls a host script function synchronously; missing functions become NotSupportedException.</summary>
    private JsonElement Call(string function, object? args, [CallerMemberName] string method = "")
    {
        if (!_api.Functions.Any(f => string.Equals(f.Name, function, StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException(
                $"CPH.{method} is not supported without the '{function}' script function, which no loaded Cuebot module provides");
        }
        var result = _api.CallAsync(RequireContext(method), function, args, _ct).GetAwaiter().GetResult();
        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>
    /// Calls a script function that reports results by writing action arguments (the sub-action style "get"
    /// functions) and returns the arguments as they were right after the call. The run's arguments are then put
    /// back, so a CPH method that returns data does not also leave the sub-action's arguments behind.
    /// </summary>
    private Dictionary<string, object?> CallForArgs(string function, object? args, [CallerMemberName] string method = "")
    {
        var context = RequireContext(method);
        var before = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        try
        {
            Call(function, args, method);
            return new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var key in context.Args.Keys.Where(k => !before.ContainsKey(k)).ToList()) context.Args.Remove(key);
            foreach (var (key, value) in before) context.Args[key] = value;
        }
    }

    private bool HasFunction(string function) =>
        _api.Functions.Any(f => string.Equals(f.Name, function, StringComparison.OrdinalIgnoreCase));

    private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Sync(Task task) => task.GetAwaiter().GetResult();

    private static string Text(object? value) => Core.Actions.VariableResolver.Format(value);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
