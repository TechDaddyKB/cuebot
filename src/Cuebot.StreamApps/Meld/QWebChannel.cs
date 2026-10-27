using System.Globalization;
using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Meld;

/// <summary>
/// Qt WebChannel client protocol (the JSON messages qwebchannel.js exchanges with a QWebChannel server), implemented
/// from Qt's documented message types. One JSON object per websocket text frame.
/// </summary>
internal static class QWebChannel
{
    public const int Signal = 1;
    public const int PropertyUpdate = 2;
    public const int Init = 3;
    public const int Idle = 4;
    public const int Debug = 5;
    public const int InvokeMethod = 6;
    public const int ConnectToSignal = 7;
    public const int DisconnectFromSignal = 8;
    public const int SetProperty = 9;
    public const int Response = 10;

    public static string InitMessage(long id) => JsonUtil.Serialize(new { type = Init, id });

    public static string IdleMessage() => JsonUtil.Serialize(new { type = Idle });

    public static string Invoke(long id, string objectName, int method, IReadOnlyList<object?> args) =>
        JsonUtil.Serialize(new { type = InvokeMethod, @object = objectName, method, args, id });

    /// <summary>Invoke by name: Qt 6 servers resolve plain method names (used when init did not list the method).</summary>
    public static string InvokeByName(long id, string objectName, string method, IReadOnlyList<object?> args) =>
        JsonUtil.Serialize(new { type = InvokeMethod, @object = objectName, method, args, id });

    public static string Connect(string objectName, int signal) =>
        JsonUtil.Serialize(new { type = ConnectToSignal, @object = objectName, signal });

    /// <summary>The messages in one frame (a single object, or defensively an array of them).</summary>
    public static IEnumerable<JsonElement> Messages(string text)
    {
        if (JsonUtil.TryParse(text) is not { } root) yield break;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    public static int Type(JsonElement message) =>
        message.TryGetProperty("type", out var t) && t.TryGetInt32(out var type) ? type : 0;

    /// <summary>The request id of a response (numbers, or strings from servers that echo them).</summary>
    public static long? Id(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id)) return null;
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n)) return n;
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }
}

/// <summary>A published QObject as described by the init response: methods, properties (cached values) and signals.</summary>
internal sealed class QWebChannelObject
{
    private readonly Dictionary<string, int> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _signals = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _signalNames = [];
    private readonly Dictionary<int, string> _propertyNames = [];
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    public QWebChannelObject(string name, JsonElement description)
    {
        Name = name;
        if (description.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
            foreach (var m in methods.EnumerateArray())
                if (Pair(m) is var (methodName, index)) _methods.TryAdd(StripSignature(methodName), index);
        if (description.TryGetProperty("signals", out var signals) && signals.ValueKind == JsonValueKind.Array)
            foreach (var s in signals.EnumerateArray())
                if (Pair(s) is var (signalName, index)) AddSignal(StripSignature(signalName), index);
        if (description.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in properties.EnumerateArray())
            {
                // [propertyIndex, "name", [notifySignalName | 1, notifySignalIndex] | null, value]
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 4) continue;
                if (!p[0].TryGetInt32(out var index) || p[1].ValueKind != JsonValueKind.String) continue;
                var propertyName = p[1].GetString()!;
                _propertyNames[index] = propertyName;
                _values[propertyName] = p[3].Clone();
                var notify = p[2];
                if (notify.ValueKind == JsonValueKind.Array && notify.GetArrayLength() >= 2 && notify[1].TryGetInt32(out var signalIndex))
                {
                    // Qt sends 1 instead of the name when it is "<property>Changed".
                    var signalName = notify[0].ValueKind == JsonValueKind.String ? notify[0].GetString()! : propertyName + "Changed";
                    AddSignal(StripSignature(signalName), signalIndex);
                }
            }
        }
    }

    public string Name { get; }

    public IReadOnlyCollection<string> MethodNames => _methods.Keys;

    public IReadOnlyCollection<string> PropertyNames => _values.Keys;

    public bool HasMethod(string name) => _methods.ContainsKey(name);

    public int? MethodIndex(string name) => _methods.TryGetValue(name, out var i) ? i : null;

    public int? SignalIndex(string name) => _signals.TryGetValue(name, out var i) ? i : null;

    public string? SignalName(int index) => _signalNames.GetValueOrDefault(index);

    public JsonElement? Property(string name) => _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>Applies a propertyUpdate entry; returns the names of the properties that changed.</summary>
    public IReadOnlyList<string> ApplyUpdate(JsonElement properties)
    {
        var changed = new List<string>();
        if (properties.ValueKind != JsonValueKind.Object) return changed;
        foreach (var p in properties.EnumerateObject())
        {
            if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;
            if (!_propertyNames.TryGetValue(index, out var name)) continue;
            _values[name] = p.Value.Clone();
            changed.Add(name);
        }
        return changed;
    }

    private void AddSignal(string name, int index)
    {
        _signals.TryAdd(name, index);
        _signalNames.TryAdd(index, name);
    }

    private static (string Name, int Index)? Pair(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2 && entry[0].ValueKind == JsonValueKind.String && entry[1].TryGetInt32(out var i)
            ? (entry[0].GetString()!, i)
            : null;

    private static string StripSignature(string name) => name.IndexOf('(') is var p and > 0 ? name[..p] : name;
}
