using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Sends any obs-websocket request and stores the raw <c>responseData</c> JSON in an argument.</summary>
public sealed class RawRequestSubAction(ObsService obs) : ISubActionHandler
{
    public string TypeId => "obs.rawRequest";
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName => "Raw request";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("requestType", "Request type") { Help = "An obs-websocket 5 request type, e.g. GetStats" },
        new("requestData", "Request data (JSON)", ParameterKind.Multiline) { Help = "A JSON object; leave empty for none" },
        new("resultArgument", "Store response in argument") { DefaultValue = "obsResult" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var requestType = ObsSubActionSupport.Require(p["requestType"], "Request type");
        var response = await obs.RequestAsync(requestType, ParseRequestData(p["requestData"]), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(p["resultArgument"])) context.Args[p["resultArgument"]] = response.GetRawText();
        return SubActionResult.Next;
    }

    /// <summary>Parses the optional request data; null when blank.</summary>
    internal static JsonElement? ParseRequestData(string json)
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
            throw new ArgumentException($"Request data is not valid JSON: {ex.Message}", ex);
        }
        return root.ValueKind == JsonValueKind.Object ? root : throw new ArgumentException("Request data must be a JSON object.");
    }
}
