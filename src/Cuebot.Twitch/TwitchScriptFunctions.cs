using Cuebot.Core.Scripting;

namespace Cuebot.Twitch;

/// <summary>Twitch functions available to user scripts.</summary>
internal sealed class TwitchScriptFunctions(TwitchService twitch) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("twitch.sendMessage", "Send a message to Twitch chat", ["message"], async (_, args, ct) =>
        {
            await twitch.SendChatMessageAsync(args.GetString("message"), ct).ConfigureAwait(false);
            return null;
        }),
    ];
}
