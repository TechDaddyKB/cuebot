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
            ["requests", "haltOnFailure", "executionType", "connection"],
            async (_, args, ct) =>
            {
                if (args.GetElement("requests") is not { ValueKind: JsonValueKind.Array } requests)
                    throw new ScriptException("obs.requestBatch needs a requests array");
                return await obs.Get(args.GetString("connection"))
                    .RequestBatchAsync(requests.EnumerateArray().Select(r => (object)r).ToArray(), args.GetBool("haltOnFailure"), ct,
                        ParseExecution(args.GetString("executionType")))
                    .ConfigureAwait(false);
            }),
        new("obs.status", "Connection, stream, recording, replay buffer, virtual camera, studio mode and scene, without asking OBS",
            ["connection"], async (_, args, ct) =>
                await obs.Get(args.GetString("connection")).GetStatusAsync(ct).ConfigureAwait(false)),
        new("obs.sendCustomEvent", "Broadcast a custom event to every obs-websocket client", ["eventData", "connection"],
            async (_, args, ct) =>
            {
                if (args.GetElement("eventData") is not { ValueKind: JsonValueKind.Object } data)
                    throw new ScriptException("obs.sendCustomEvent needs an eventData object");
                await obs.Get(args.GetString("connection")).BroadcastCustomEventAsync(data, ct).ConfigureAwait(false);
                return null;
            }),
        new("obs.callVendorRequest", "Call a request an OBS plugin registered; returns its responseData",
            ["vendorName", "requestType", "requestData", "connection"],
            async (_, args, ct) =>
            {
                var vendor = args.GetString("vendorName");
                var type = args.GetString("requestType");
                if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(type))
                    throw new ScriptException("obs.callVendorRequest needs a vendorName and a requestType");
                var data = args.GetElement("requestData") is { ValueKind: JsonValueKind.Object } d ? d : (JsonElement?)null;
                return await obs.Get(args.GetString("connection")).CallVendorRequestAsync(vendor, type, data, ct).ConfigureAwait(false);
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

    /// <summary>"SerialFrame", "1", "parallel"... (-1, None or empty: the default SerialRealtime).</summary>
    internal static Protocol.ObsBatchExecution ParseExecution(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text == "-1" || text.Equals("None", StringComparison.OrdinalIgnoreCase)) return Protocol.ObsBatchExecution.SerialRealtime;
        if (int.TryParse(text, out var number) && Enum.IsDefined((Protocol.ObsBatchExecution)number)) return (Protocol.ObsBatchExecution)number;
        var compact = text.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse<Protocol.ObsBatchExecution>(compact, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ScriptException($"executionType must be SerialRealtime (0), SerialFrame (1) or Parallel (2), not '{value}'");
    }
}
