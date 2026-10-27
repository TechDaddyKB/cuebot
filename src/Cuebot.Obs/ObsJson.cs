using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>Lenient readers for OBS payloads, which omit fields freely.</summary>
internal static class ObsJson
{
    public static string? GetStringOrNull(this JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
