using Cuebot.Core.Chat.Emotes;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Speech.Emotes;

/// <summary>
/// Supplies one third-party provider's emote names (from the core <see cref="EmoteService"/>) so spoken chat can strip
/// 7TV, BTTV and FFZ emotes like platform emotes.
/// </summary>
public sealed class ThirdPartyEmoteNames(EmoteService emotes, string provider) : IEmoteNameProvider
{
    public string Source => provider;

    public IReadOnlyCollection<string> GetEmoteNames(string platform)
    {
        emotes.EnsureStarted();
        return emotes.GetThirdPartyEmotes(platform).Values
            .Where(e => e.Provider == provider)
            .Select(e => e.Code)
            .ToArray();
    }
}

public static class SpeechEmoteServiceCollectionExtensions
{
    /// <summary>Registers 7TV, BTTV and FFZ emote names for speech. Needs <c>AddChatExtrasFeatures</c> (core).</summary>
    public static IServiceCollection AddSpeechEmoteNames(this IServiceCollection services)
    {
        foreach (var provider in EmoteProviders.ThirdParty)
            services.AddSingleton<IEmoteNameProvider>(sp => new ThirdPartyEmoteNames(sp.GetRequiredService<EmoteService>(), provider));
        return services;
    }
}
