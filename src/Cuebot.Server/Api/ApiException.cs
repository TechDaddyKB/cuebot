using System.Text.Json.Nodes;

namespace Cuebot.Server.Api;

/// <summary>An API operation failed; becomes an HTTP status with <c>{error, details?}</c> (or an MCP tool error).</summary>
public sealed class ApiException(int status, string message, JsonArray? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public JsonArray? Details { get; } = details;

    public static ApiException BadRequest(string message, JsonArray? details = null) => new(400, message, details);
    public static ApiException NotFound(string message) => new(404, message);
    public static ApiException Conflict(string message) => new(409, message);
    public static ApiException Unavailable(string message) => new(503, message);

    public JsonObject ToJson()
    {
        var body = new JsonObject { ["error"] = Message };
        if (Details is { Count: > 0 }) body["details"] = Details.DeepClone();
        return body;
    }
}
