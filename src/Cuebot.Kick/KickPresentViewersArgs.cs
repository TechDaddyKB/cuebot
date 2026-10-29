using Cuebot.Core.Users;
using Cuebot.Kick.Events;

namespace Cuebot.Kick;

/// <summary>Adds the Kick stream's details to Kick's Present Viewers events (see <see cref="KickService.AddPresentViewersArgs"/>).</summary>
internal sealed class KickPresentViewersArgs(KickService kick) : IPresentViewersArgs
{
    public string Platform => KickArgs.Platform;

    public void Add(IDictionary<string, object?> args) => kick.AddPresentViewersArgs(args);
}
