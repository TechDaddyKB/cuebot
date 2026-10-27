using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.Obs;

/// <summary>Exposes OBS to user scripts. Every function takes an optional <c>connection</c> name (default connection when empty).</summary>
public sealed class ObsScriptFunctions(ObsService obs) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("obs.request", "Send an obs-websocket request; returns its responseData", ["requestType", "requestData", "connection"],
            async (_, args, ct) =>
            {
                var requestType = args.GetString("requestType");
                if (string.IsNullOrWhiteSpace(requestType)) throw new ScriptException("obs.request needs a requestType");
                var requestData = args.GetElement("requestData") is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } data
                    ? data
                    : (JsonElement?)null;
                return await obs.Get(args.GetString("connection")).RequestAsync(requestType, requestData, ct).ConfigureAwait(false);
            }),
        new("obs.requestBatch", "Send an obs-websocket request batch; returns the results array",
            ["requests", "haltOnFailure", "connection"],
            async (_, args, ct) =>
            {
                if (args.GetElement("requests") is not { ValueKind: JsonValueKind.Array } requests)
                    throw new ScriptException("obs.requestBatch needs a requests array");
                return await obs.Get(args.GetString("connection"))
                    .RequestBatchAsync(requests.EnumerateArray().Select(r => (object)r).ToArray(), args.GetBool("haltOnFailure"), ct)
                    .ConfigureAwait(false);
            }),
        new("obs.setScene", "Switch the program scene", ["sceneName", "connection"], async (_, args, ct) =>
        {
            await obs.Get(args.GetString("connection")).SetCurrentSceneAsync(args.GetString("sceneName"), ct).ConfigureAwait(false);
            return null;
        }),
        new("obs.getCurrentScene", "The current program scene", ["connection"], async (_, args, ct) =>
            await obs.Get(args.GetString("connection")).GetCurrentSceneAsync(ct).ConfigureAwait(false)),
        new("obs.connections", "Names and states of all OBS connections", [], (_, _, _) =>
            Task.FromResult<object?>(obs.Connections.Select(c => new { name = c.Name, state = c.State.ToString(), scene = c.CurrentScene }).ToArray())),
    ];
}
