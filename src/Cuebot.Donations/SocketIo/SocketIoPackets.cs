using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.SocketIo;

/// <summary>Engine.IO packet types (the first character of every websocket text frame).</summary>
internal enum EngineType { Open = '0', Close = '1', Ping = '2', Pong = '3', Message = '4', Upgrade = '5', Noop = '6' }

/// <summary>Socket.IO packet types (the character after the Engine.IO "4").</summary>
internal enum SocketIoType { Connect = 0, Disconnect = 1, Event = 2, Ack = 3, ConnectError = 4, BinaryEvent = 5, BinaryAck = 6 }

/// <summary>The Engine.IO handshake ("0{...}").</summary>
internal sealed record EngineOpen(string Sid, TimeSpan PingInterval, TimeSpan PingTimeout);

/// <summary>A decoded Socket.IO packet: <c>4</c> + type + optional <c>/namespace,</c> + optional ack id + JSON.</summary>
internal sealed record SocketIoPacket(SocketIoType Type, string Namespace, long? AckId, string Data)
{
    /// <summary>For EVENT packets: the event name and its arguments (cloned, safe to keep).</summary>
    public (string Name, IReadOnlyList<JsonElement> Args)? ReadEvent()
    {
        if (Type is not (SocketIoType.Event or SocketIoType.BinaryEvent)) return null;
        if (JsonReaders.TryParse(Data) is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0) return null;
        var items = array.EnumerateArray().ToArray();
        if (items[0].ValueKind != JsonValueKind.String) return null;
        return (items[0].GetString()!, items[1..]);
    }

    /// <summary>The message of a CONNECT_ERROR ("44{"message":"..."}" in v3+, "44\"...\"" in v2).</summary>
    public string ErrorMessage()
    {
        if (JsonReaders.TryParse(Data) is not { } json) return Data;
        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString() ?? "",
            JsonValueKind.Object => json.Str("message") is { Length: > 0 } m ? m : json.GetRawText(),
            _ => json.GetRawText(),
        };
    }
}

/// <summary>Pure Engine.IO / Socket.IO framing (websocket transport, text packets only).</summary>
internal static class SocketIoFraming
{
    public const string Ping = "2";
    public const string Pong = "3";

    /// <summary>"40" (EIO 4 clients must connect the default namespace themselves), optionally with an auth payload.</summary>
    public static string Connect(string? authJson = null) => "40" + (authJson ?? "");

    public static string Event(string name, params object?[] args)
    {
        var items = new List<object?> { name };
        items.AddRange(args);
        return "42" + JsonSerializer.Serialize(items);
    }

    /// <summary>The websocket URL for a Socket.IO server, e.g. https://sockets.streamlabs.com + query.</summary>
    public static Uri Url(Uri server, int eio, IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme is "https" or "wss" ? "wss" : "ws",
            Port = server.IsDefaultPort ? -1 : server.Port,
            Path = server.AbsolutePath.TrimEnd('/') + "/socket.io/",
        };
        var parts = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
            .Append($"EIO={eio.ToString(CultureInfo.InvariantCulture)}")
            .Append("transport=websocket");
        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    public static EngineType? TypeOf(string frame) =>
        frame.Length > 0 && frame[0] is >= '0' and <= '6' ? (EngineType)frame[0] : null;

    public static EngineOpen? ParseOpen(string frame)
    {
        if (TypeOf(frame) != EngineType.Open || JsonReaders.TryParse(frame[1..]) is not { ValueKind: JsonValueKind.Object } json) return null;
        var interval = json.Number("pingInterval");
        var timeout = json.Number("pingTimeout");
        return new EngineOpen(json.Str("sid"),
            TimeSpan.FromMilliseconds(interval > 0 ? interval : 25000),
            TimeSpan.FromMilliseconds(timeout > 0 ? timeout : 20000));
    }

    /// <summary>Decodes an Engine.IO MESSAGE frame ("4...") into a Socket.IO packet.</summary>
    public static SocketIoPacket? ParsePacket(string frame)
    {
        if (frame.Length < 2 || frame[0] != '4' || frame[1] is < '0' or > '6') return null;
        var type = (SocketIoType)(frame[1] - '0');
        var i = 2;
        if (type is SocketIoType.BinaryEvent or SocketIoType.BinaryAck)
        {
            // "<attachments>-" precedes the namespace for binary packets.
            var dash = frame.IndexOf('-', i);
            if (dash < 0) return null;
            i = dash + 1;
        }
        var ns = "/";
        if (i < frame.Length && frame[i] == '/')
        {
            var comma = frame.IndexOf(',', i);
            if (comma < 0) return new SocketIoPacket(type, frame[i..], null, "");
            ns = frame[i..comma];
            i = comma + 1;
        }
        var start = i;
        while (i < frame.Length && char.IsAsciiDigit(frame[i])) i++;
        long? ack = i > start && long.TryParse(frame.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        return new SocketIoPacket(type, ns, ack, frame[i..]);
    }
}
