using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Shared constants and parameter parsing for OBS (and Streamlabs Desktop) sub-actions and triggers.</summary>
internal static class ObsSubActionSupport
{
    public const string Category = "OBS";

    /// <summary>Choice values offered for an enum parameter, in declaration order.</summary>
    public static IReadOnlyList<string> ChoicesOf<TEnum>() where TEnum : struct, Enum => Enum.GetNames<TEnum>();

    /// <summary>Parses a Choice parameter value (case-insensitive), failing with a message naming the valid options.</summary>
    public static TEnum ParseChoice<TEnum>(string value, string label) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _)
            ? parsed
            : throw new ArgumentException($"{label} must be one of {string.Join(", ", ChoicesOf<TEnum>())} (got '{value}').");

    public static string Require(string value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{label} is required.") : value;

    /// <summary>A number, or null when the text is blank.</summary>
    public static double? OptionalNumber(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"{label} must be a number (got '{value}').");
    }

    public static double RequireNumber(string value, string label) =>
        OptionalNumber(value, label) ?? throw new ArgumentException($"{label} is required.");

    /// <summary>Parses a JSON object parameter (null when blank unless required).</summary>
    public static JsonElement? ParseJson(string json, string label, JsonValueKind kind = JsonValueKind.Object)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{label} is not valid JSON: {ex.Message}", ex);
        }
        return root.ValueKind == kind
            ? root
            : throw new ArgumentException($"{label} must be a JSON {(kind == JsonValueKind.Array ? "array" : "object")}.");
    }

    /// <summary>Stores <paramref name="value"/> in the argument named by the parameter, when one is given.</summary>
    public static void Store(ActionRunContext context, string argumentName, object? value)
    {
        if (!string.IsNullOrWhiteSpace(argumentName)) context.Args[argumentName.Trim()] = value;
    }

    /// <summary>Copies every property of a JSON object into args as <c>prefix + PropertyName</c>.</summary>
    public static void StoreObject(ActionRunContext context, string prefix, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        foreach (var property in data.EnumerateObject())
            context.Args[prefix + char.ToUpperInvariant(property.Name[0]) + property.Name[1..]] = property.Value.ToArgValue();
    }
}
