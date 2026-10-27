using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.YouTube;

/// <summary>Registers the YouTube module.</summary>
public static class YouTubeServiceCollectionExtensions
{
    public static IServiceCollection AddYouTube(this IServiceCollection services) => services;
}
