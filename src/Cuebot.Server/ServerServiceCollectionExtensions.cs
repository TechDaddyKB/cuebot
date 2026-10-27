using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Server.Custom;
using Cuebot.Server.SpeakerBot;
using Cuebot.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Server;

/// <summary>Registers Cuebot's local API servers (WebSocket, HTTP, UDP) and compatibility endpoints.</summary>
/// <remarks>The host starts them with <c>Get&lt;ServerManager&gt;().StartAsync()</c>; all are off until enabled.</remarks>
public static class ServerServiceCollectionExtensions
{
    public static IServiceCollection AddServers(this IServiceCollection services)
    {
        services.AddSettingsFile<ServerSettings>("servers.json");
        services.AddSingleton<ISpeakerBackend>(sp => new SpeechServiceBackend(sp.GetRequiredService<SpeechService>()));
        services.AddSingleton(sp => new ServerServices(sp));
        services.AddSingleton(sp => new ServerManager(sp.GetRequiredService<JsonFile<ServerSettings>>(), sp.GetRequiredService<ServerServices>()));

        services.AddSingleton<ITriggerProvider, CustomWebSocketTriggers>();
        services.AddSingleton<ISubActionHandler, CustomServerSendSubAction>();
        services.AddSingleton<ISubActionHandler, CustomServerStateSubAction>();
        services.AddSingleton<ISubActionHandler, CustomClientSendSubAction>();
        services.AddSingleton<ISubActionHandler, CustomClientStateSubAction>();
        services.AddSingleton<ISubActionHandler, WebSocketBroadcastSubAction>();
        services.AddSingleton<IScriptFunctionProvider, WebSocketScriptFunctions>();
        return services;
    }
}
