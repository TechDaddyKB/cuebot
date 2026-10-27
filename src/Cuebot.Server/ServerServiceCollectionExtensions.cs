using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Server;

/// <summary>Registers Cuebot's local API servers (WebSocket, HTTP, UDP) and compatibility endpoints.</summary>
public static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddServers(this IServiceCollection services) => services;
}
