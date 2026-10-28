using Streamer.bot.Common.Events;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// <c>__source</c> as Streamer.bot C# sees it. Cuebot.Core stores the Streamer.bot trigger code (an int, 0 = none);
/// while a C# step runs, the argument holds the matching <see cref="EventType"/> instead, so pasted code such as
/// <c>(EventType)args["__source"] == EventType.TwitchFollow</c> or <c>args["__source"].ToString() == "TwitchFollow"</c>
/// works. Afterwards the number is put back, so later steps and <c>%__source%</c> see what Core set.
/// </summary>
internal static class SourceArgument
{
    public const string Name = "__source";

    /// <summary>Converts <c>__source</c> for the script; dispose to convert it back.</summary>
    public static IDisposable Enter(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue(Name, out var value) || ToEventType(value) is not { } type) return Restore.None;
        args[Name] = type;
        return new Restore(args);
    }

    /// <summary>The event type for a stored <c>__source</c> value (a code, or an EventType already).</summary>
    public static EventType? ToEventType(object? value) => value switch
    {
        EventType type => type,
        int code => Defined(code),
        long code and >= int.MinValue and <= int.MaxValue => Defined((int)code),
        _ => null,
    };

    private static EventType Defined(int code) => Enum.IsDefined(typeof(EventType), code) ? (EventType)code : EventType.None;

    private sealed class Restore(Dictionary<string, object?>? args) : IDisposable
    {
        public static readonly Restore None = new(null);

        public void Dispose()
        {
            if (args is not null && args.TryGetValue(Name, out var value) && value is EventType type) args[Name] = (int)type;
        }
    }
}
