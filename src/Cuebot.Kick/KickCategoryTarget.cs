using Cuebot.Core.Games;

namespace Cuebot.Kick;

/// <summary>Kick's category for the category switcher.</summary>
internal sealed class KickCategoryTarget(KickService kick) : IStreamCategoryTarget
{
    public string Platform => "Kick";

    public bool CanSet => kick.IsBroadcasterSignedIn;

    public async Task<string?> CurrentAsync(CancellationToken ct = default) =>
        (await kick.RefreshChannelAsync(ct).ConfigureAwait(false))?.Category?.Name;

    public async Task<string?> FindAsync(string game, CancellationToken ct = default)
    {
        var found = await kick.FindCategoryAsync(game, ct).ConfigureAwait(false);
        // Kick's search returns its best guess for anything; only a close name counts as this game's category.
        return found is not null && CategoryNames.Similar(found.Name, game) ? found.Name : null;
    }

    public async Task<string> SetAsync(string category, CancellationToken ct = default) =>
        (await kick.SetCategoryAsync(category, ct).ConfigureAwait(false)).Name;
}
