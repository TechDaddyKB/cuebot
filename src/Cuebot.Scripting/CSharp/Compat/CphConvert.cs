using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Scripting.Compat;

/// <summary>Value conversions between Cuebot's stored values and the types Streamer.bot scripts ask for.</summary>
public static class CphConvert
{
    /// <summary>
    /// The value to store for a Streamer.bot <c>Set*Var(object)</c>: text, numbers and booleans as they are; anything
    /// else (lists, objects) as JSON text, which <see cref="TryTo{T}"/> deserializes again.
    /// </summary>
    public static object? ToStored(object? value) => value switch
    {
        null or string or bool or long or int or short or byte or sbyte or ushort or uint or ulong
            or decimal or double or float => value,
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        Enum e => e.ToString(),
        JsonElement e => e,
        _ => JsonSerializer.Serialize(value),
    };

    public static T? To<T>(object? value) => TryTo(value, out T result) ? result : default;

    public static bool TryTo<T>(object? value, out T result)
    {
        result = default!;
        if (value is null) return !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null;
        if (value is T same)
        {
            result = same;
            return true;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            object? converted;
            if (target == typeof(string)) converted = VariableResolver.Format(value);
            else if (target == typeof(object)) converted = value;
            else if (value is JsonElement json) converted = json.Deserialize<T>();
            else if (target.IsEnum) converted = Enum.Parse(target, value.ToString()!, ignoreCase: true);
            else if (target == typeof(Guid)) converted = Guid.Parse(value.ToString()!);
            else if (target == typeof(bool) && value is string b) converted = bool.Parse(b.Trim());
            else if (target == typeof(DateTime) && value is string dt) converted = DateTime.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            else if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
                converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            else if (value is string text) converted = JsonSerializer.Deserialize<T>(text);
            else converted = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));

            if (converted is null) return !typeof(T).IsValueType;
            result = (T)converted;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or JsonException or OverflowException
                                       or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
