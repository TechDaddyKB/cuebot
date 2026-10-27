using Cuebot.Core.Scripting;

namespace Cuebot.Rumble;

/// <summary>Rumble functions available to user scripts.</summary>
internal sealed class RumbleScriptFunctions(RumbleService rumble) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("rumble.sendMessage", "Send a message to Rumble chat", ["message"], async (_, args, ct) =>
        {
            await rumble.SendChatMessageAsync(args.GetString("message"), ct).ConfigureAwait(false);
            return null;
        }),
    ];
}
