using Cuebot.Core.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.BuiltIn;

/// <summary>Registration for system sub-actions and services (programs, files, HTTP, JSON, watchers).</summary>
internal static class SystemFeatures
{
    public static IServiceCollection AddSystemFeatures(this IServiceCollection services)
    {
        services.AddSingleton<ISubActionHandler, RunProgramSubAction>();
        return services;
    }
}
