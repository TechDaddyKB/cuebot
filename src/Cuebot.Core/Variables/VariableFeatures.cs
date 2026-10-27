using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Variables;

/// <summary>Registration for arguments, global and per-user variables, and text functions.</summary>
internal static class VariableFeatures
{
    public static IServiceCollection AddVariableFeatures(this IServiceCollection services)
    {
        services.AddSingleton<ISubActionHandler, SetArgumentSubAction>();
        services.AddSingleton<ISubActionHandler, SetGlobalSubAction>();
        services.AddSingleton<ISubActionHandler, RandomNumberSubAction>();
        return services;
    }
}
