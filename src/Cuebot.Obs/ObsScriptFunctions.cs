using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.Obs;

/// <summary>Exposes <c>obs.request</c> to user scripts.</summary>
public sealed class ObsScriptFunctions(ObsService obs) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("obs.request", "Send an obs-websocket request; returns its responseData", ["requestType", "requestData"],
            async (_, args, ct) =>
            {
                var requestType = args.GetString("requestType");
                if (string.IsNullOrWhiteSpace(requestType)) throw new ScriptException("obs.request needs a requestType");
                var requestData = args.GetElement("requestData") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } data
                    ? data
                    : (JsonElement?)null;
                return await obs.RequestAsync(requestType, requestData, ct).ConfigureAwait(false);
            }),
    ];
}
