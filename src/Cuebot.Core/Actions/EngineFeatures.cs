using Cuebot.Core.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Actions;

/// <summary>Registration for the action engine: flow and logic sub-actions, queues, history.</summary>
internal static class EngineFeatures
{
    public static IServiceCollection AddEngineFeatures(this IServiceCollection services)
    {
        services.AddSingleton<ISubActionHandler, DelaySubAction>();
        services.AddSingleton<ISubActionHandler, LogSubAction>();
        services.AddSingleton<ISubActionHandler, IfSubAction>();
        services.AddSingleton<ISubActionHandler, StopSubAction>();
        services.AddSingleton<ISubActionHandler, RunActionSubAction>();
        services.AddSingleton<ITriggerProvider, CoreTriggers>();
        return services;
    }
}
