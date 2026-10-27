using System.Text.Json;

namespace Cuebot.Obs.SubActions;

/// <summary>Parsing for the raw request sub-actions.</summary>
internal static class RawRequestSubAction
{
    /// <summary>Parses the optional request data; null when blank.</summary>
    internal static JsonElement? ParseRequestData(string json) => ObsSubActionSupport.ParseJson(json, "Request data");

    /// <summary>
    /// Parses a batch: a JSON array of <c>{"requestType": "...", "requestData": {...}}</c> objects.
    /// </summary>
    internal static IReadOnlyList<object> ParseBatch(string json)
    {
        var array = ObsSubActionSupport.ParseJson(json, "Requests", JsonValueKind.Array)
                    ?? throw new ArgumentException("Requests is required.");
        var requests = new List<object>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.GetStringOrNull("requestType") is not { Length: > 0 })
                throw new ArgumentException("Each request must be an object with a requestType.");
            requests.Add(item);
        }
        return requests.Count == 0 ? throw new ArgumentException("Requests must not be empty.") : requests;
    }
}
