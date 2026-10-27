using Cuebot.Core.Scripting;

namespace Cuebot.Speech;

/// <summary>Exposes speech to user scripts.</summary>
internal sealed class SpeechScriptFunctions(SpeechService speech) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("speech.speak", "Speak a text with an optional voice alias; wait=true returns once it has been spoken",
            ["text", "alias", "wait"], async (_, args, ct) =>
            {
                var alias = args.GetString("alias");
                await speech.SpeakAsync(args.GetString("text"), alias.Length == 0 ? null : alias, args.GetBool("wait"), ct)
                    .ConfigureAwait(false);
                return null;
            }),
    ];
}
