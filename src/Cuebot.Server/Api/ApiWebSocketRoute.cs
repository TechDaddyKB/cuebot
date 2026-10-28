using Cuebot.Server.Hosting;

namespace Cuebot.Server.Api;

/// <summary>
/// A WebSocket endpoint served on the REST API listener at <c>/api/v1/{Name}</c>, behind the same bearer token and origin
/// rules as the rest of the API. Modules register implementations in DI (e.g. the Stream Deck plugin channel at
/// <c>/api/v1/streamdeck</c>) so they get an authenticated local socket without opening another port.
/// </summary>
/// <remarks>
/// The callbacks run on the connection's own task. Exceptions thrown by <see cref="IWebSocketHandler.OnMessageAsync"/>
/// are logged and close that connection.
/// </remarks>
public interface IApiWebSocketRoute : IWebSocketHandler
{
    /// <summary>The path segment after <c>/api/v1/</c>, e.g. "streamdeck". Matched case-insensitively.</summary>
    string Name { get; }
}
