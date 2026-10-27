using Cuebot.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Commands;

/// <summary>Registration for the commands manager, users and groups, and timers.</summary>
internal static class CommandAndUserFeatures
{
    public static IServiceCollection AddCommandAndUserFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new TimerService(sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<Events.EventBus>()));
        return services;
    }
}
