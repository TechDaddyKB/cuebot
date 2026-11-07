using Cuebot.Core;
using Cuebot.Core.Profiles;
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
        // Shared by every streamer profile: one set of ports and one API token per computer, so OBS docks and the paired
        // phone keep working after a switch.
        services.AddSingleton(sp => new JsonFile<ServerSettings>(sp.GetRequiredService<AppPaths>().SharedFile(ProfileCatalog.ServerSettingsFile),
            sp.GetRequiredService<Cuebot.Core.Logging.AppLog>()));
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
