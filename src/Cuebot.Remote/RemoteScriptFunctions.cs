using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.Remote;

/// <summary>Remote signal functions for user scripts.</summary>
internal sealed class RemoteScriptFunctions(RemoteHub hub) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("remote.sendSignal", "Send a signal to a remote instance (instance empty = all connected); args is an object. Returns how many instances got it",
            ["name", "instance", "args"], async (context, a, ct) =>
            {
                var name = a.GetString("name").Trim();
                if (name.Length == 0) throw new ScriptException("name is required");
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (a.GetElement("args") is { ValueKind: JsonValueKind.Object } obj)
                {
                    foreach (var property in obj.EnumerateObject()) args[property.Name] = ScriptArgs.ToClr(property.Value);
                }
                return await hub.SendSignalAsync(a.GetString("instance"), name, context.Action.Name, args, ct).ConfigureAwait(false);
            }),
        new("remote.waitForSignal", "Wait for a signal from a remote instance (instance empty = any; timeoutMs default 30000, 0 = forever). Returns {name, sender, instance..., args} or null on timeout",
            ["name", "instance", "timeoutMs"], async (_, a, ct) =>
            {
                var name = a.GetString("name").Trim();
                if (name.Length == 0) throw new ScriptException("name is required");
                var timeoutMs = int.TryParse(a.GetString("timeoutMs", "30000"), out var t) ? t : 30000;
                var signal = await hub.WaitForSignalAsync(name, a.GetString("instance"), timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : null, ct)
                    .ConfigureAwait(false);
                return signal is null ? null : new
                {
                    name = signal.Name, sender = signal.Sender, instanceId = signal.From.Id, instanceName = signal.From.Name,
                    instanceLabel = signal.Label, instanceOs = signal.From.Os, instanceVersion = signal.From.Version, args = signal.Args,
                };
            }),
        new("remote.getInstances", "Configured and connected remote instances with their status", [],
            (_, _, _) => Task.FromResult<object?>(hub.Instances.Select(i => new
            {
                name = i.Name, outgoing = i.Outgoing, state = i.State.ToString(), message = i.Message,
                instanceId = i.Peer?.Id, instanceName = i.Peer?.Name, instanceOs = i.Peer?.Os, instanceVersion = i.Peer?.Version,
                connectedAt = i.ConnectedAt?.ToString("o"),
            }).ToArray())),
    ];
}
