using Cuebot.Core.Scripting;

namespace Cuebot.Twitch;

/// <summary>
/// Twitch functions available to user scripts: every sub-action under its own id (twitch.sendMessage,
/// twitch.ban, twitch.createPoll, ...) plus account and reward listings.
/// </summary>
internal sealed class TwitchScriptFunctions(TwitchService twitch) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        .. TwitchSubActions.ScriptFunctions(() => twitch.Api),
        new("twitch.getBroadcaster", "The signed-in broadcaster account", [], (_, _, _) => Task.FromResult<object?>(new
        {
            userId = twitch.Settings.UserId,
            userName = twitch.Settings.Login,
            connected = twitch.State == Cuebot.Core.ConnectionState.Connected,
            scopes = twitch.Settings.Scopes,
        })),
        new("twitch.getBot", "The bot account, or null when none is signed in", [], (_, _, _) => Task.FromResult<object?>(
            twitch.IsBotAuthorized
                ? new { userId = twitch.Settings.Bot.UserId, userName = twitch.Settings.Bot.Login, connected = twitch.BotState == Cuebot.Core.ConnectionState.Connected }
                : null)),
        new("twitch.getRewards", "Every channel point reward of the channel", [], async (_, _, ct) =>
            await twitch.Api.GetRewardsAsync(ct).ConfigureAwait(false)),
        new("twitch.getUser", "Look up a user by login or id:12345", ["user"], async (_, args, ct) =>
            await twitch.Api.GetUserAsync(args.GetString("user"), ct).ConfigureAwait(false)),
        .. TwitchListScriptFunctions.Create(() => twitch.Api),
    ];
}
