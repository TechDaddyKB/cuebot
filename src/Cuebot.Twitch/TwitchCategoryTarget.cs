using Cuebot.Core.Games;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>Twitch's game/category for the category switcher.</summary>
internal sealed class TwitchCategoryTarget(TwitchService twitch) : IStreamCategoryTarget
{
    public string Platform => "Twitch";

    public bool CanSet => twitch.Api.IsSignedIn;

    public async Task<string?> CurrentAsync(CancellationToken ct = default) =>
        (await twitch.Api.GetChannelAsync(null, ct).ConfigureAwait(false))?.GameName;

    public async Task<string?> FindAsync(string game, CancellationToken ct = default)
    {
        // Twitch's search returns its best guess for anything; only a close name counts as this game's category.
        var found = await twitch.Api.FindCategoryAsync(game, ct).ConfigureAwait(false);
        return found is not null && CategoryNames.Similar(found.Name, game) ? found.Name : null;
    }

    public async Task<string> SetAsync(string category, CancellationToken ct = default)
    {
        var found = await twitch.Api.FindCategoryAsync(category, ct).ConfigureAwait(false)
                    ?? throw new TwitchApiException($"No Twitch category matches '{category}'.");
        await twitch.Api.UpdateChannelAsync(new ChannelUpdate { GameId = found.Id }, ct).ConfigureAwait(false);
        return found.Name;
    }
}
