using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Kick;

/// <summary>Registers the Kick module.</summary>
public static class KickServiceCollectionExtensions
{
    public static IServiceCollection AddKick(this IServiceCollection services) => services;
}
