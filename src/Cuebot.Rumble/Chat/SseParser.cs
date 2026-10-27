using System.Text;

namespace Cuebot.Rumble.Chat;

/// <summary>
/// One server-sent event. <see cref="Type"/> is "message" unless the server named it; <see cref="Id"/> is the last
/// id the server sent, kept across events as the spec requires.
/// </summary>
internal sealed record SseEvent(string Type, string Data, string? Id);

/// <summary>
/// Incremental parser for the text/event-stream format (WHATWG HTML, "Server-sent events"): feed it one line at a
/// time (without the line terminator); a blank line dispatches the event built so far. Comment lines (": ...",
/// which Rumble sends as keep-alives) and unknown fields are ignored.
/// </summary>
internal sealed class SseParser
{
    private readonly StringBuilder _data = new();
    private string _type = "";
    private string? _id;
    private bool _hasData;

    /// <summary>Returns the completed event when <paramref name="line"/> ends one, else null.</summary>
    public SseEvent? Feed(string line)
    {
        if (line.Length == 0) return Dispatch();
        if (line[0] == ':') return null;

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];

        switch (field)
        {
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
            case "event":
                _type = value;
                break;
            case "id" when !value.Contains('\0'):
                _id = value;
                break;
        }
        return null;
    }

    /// <summary>Drops a half-received event, e.g. after the connection broke.</summary>
    public void Reset()
    {
        _data.Clear();
        _type = "";
        _hasData = false;
    }

    private SseEvent? Dispatch()
    {
        var e = _hasData ? new SseEvent(_type.Length > 0 ? _type : "message", _data.ToString(), _id) : null;
        Reset();
        return e;
    }
}
