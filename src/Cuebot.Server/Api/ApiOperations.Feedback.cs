using System.Text.Json.Nodes;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    /// <summary>
    /// Files a feature request as a public GitHub issue (MCP request_feature, always after the streamer approved the text).
    /// </summary>
    public async Task<JsonNode> RequestFeatureAsync(string title, string body, CancellationToken ct)
    {
        title = title.Trim();
        body = body.Trim();
        if (title.Length is < 5 or > 120) throw ApiException.BadRequest("The title must be 5 to 120 characters.");
        if (body.Length is < 40 or > 6000) throw ApiException.BadRequest("The description must be 40 to 6000 characters.");
        var sender = services.FeatureRequests ?? throw ApiException.Unavailable("Feature requests can only be sent from the Cuebot app.");
        var result = await sender.SendAsync(title, body, ct).ConfigureAwait(false);
        return result.Sent
            ? new JsonObject
            {
                ["sent"] = true,
                ["issue"] = result.Number,
                ["url"] = result.Url,
                ["message"] = $"Filed as issue #{result.Number}. Give the streamer the link so they can follow it.",
            }
            : new JsonObject
            {
                ["sent"] = false,
                ["error"] = result.Error,
                ["openInBrowser"] = result.BrowserUrl,
                ["message"] = "Nothing was filed. The streamer can open the link to file it on GitHub themselves (it needs a GitHub account).",
            };
    }
}
