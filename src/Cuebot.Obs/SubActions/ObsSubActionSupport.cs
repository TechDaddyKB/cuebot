namespace Cuebot.Obs.SubActions;

/// <summary>Shared constants and parameter parsing for OBS sub-actions and triggers.</summary>
internal static class ObsSubActionSupport
{
    public const string Category = "OBS";

    /// <summary>Choice values offered for an enum parameter, in declaration order.</summary>
    public static IReadOnlyList<string> ChoicesOf<TEnum>() where TEnum : struct, Enum => Enum.GetNames<TEnum>();

    /// <summary>Parses a Choice parameter value (case-insensitive), failing with a message naming the valid options.</summary>
    public static TEnum ParseChoice<TEnum>(string value, string label) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"{label} must be one of {string.Join(", ", ChoicesOf<TEnum>())} (got '{value}').");

    public static string Require(string value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{label} is required.") : value;
}
