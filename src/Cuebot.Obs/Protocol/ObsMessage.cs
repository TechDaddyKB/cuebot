using System.Text.Json;

namespace Cuebot.Obs.Protocol;

/// <summary>obs-websocket v5 message op codes used by the client.</summary>
internal enum ObsOpCode
{
    Hello = 0,
    Identify = 1,
    Identified = 2,
    Event = 5,
    Request = 6,
    RequestResponse = 7,
    RequestBatch = 8,
    RequestBatchResponse = 9,
}

/// <summary>One decoded <c>{ "op": n, "d": { ... } }</c> message.</summary>
internal sealed record ObsMessage(ObsOpCode Op, JsonElement Data)
{
    public static ObsMessage Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        var data = root.TryGetProperty("d", out var d) ? d.Clone() : default;
        return new ObsMessage((ObsOpCode)root.GetProperty("op").GetInt32(), data);
    }
}
