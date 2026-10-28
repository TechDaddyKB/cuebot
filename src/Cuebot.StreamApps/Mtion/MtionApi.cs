using System.Globalization;
using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Mtion;

/// <summary>One output parameter of an mtion external trigger ("string", "number", "bool" or "enum").</summary>
public sealed record MtionParameter(int Index, string Name, string DataType);

/// <summary>An external trigger in an mtion clubhouse.</summary>
public sealed record MtionTrigger(string Id, string Name, IReadOnlyList<MtionParameter> Parameters);

/// <summary>An mtion clubhouse and the external triggers it contains.</summary>
public sealed record MtionClubhouse(string Id, string Name, string Description, IReadOnlyList<MtionTrigger> Triggers);

/// <summary>The outcome of firing a trigger, named like Streamer.bot's documented variables.</summary>
public sealed record MtionFireResult(bool Running, bool Found, bool Sent, MtionTrigger? Trigger);

/// <summary>
/// JSON shapes of mtion studio's developer API (docs.mtion.xyz/mtion/developer-api/external-triggers, updated
/// 2024-04-17): HTTP on localhost:35393, no authentication. Documented: <c>GET /active-clubhouse</c>,
/// <c>GET /clubhouses</c>, <c>GET /clubhouse/:id</c>, <c>GET /external-trigger/triggers</c>,
/// <c>GET /external-trigger/trigger/:id</c>, and firing as <c>PATCH /external-trigger/fire-trigger/:id</c> with the
/// body <c>[{"parameter_index":0,"value":...}]</c> or <c>GET</c> on the same path with <c>?parameter_data=[...]</c>.
/// 404 = no clubhouse open, or that clubhouse / trigger not found. Cuebot uses the first two GETs, the trigger list
/// and the PATCH form.
/// </summary>
public static class MtionApi
{
    public const string ActiveClubhouse = "active-clubhouse";
    public const string Clubhouses = "clubhouses";
    public const string Triggers = "external-trigger/triggers";

    public static string FireTrigger(string id) => "external-trigger/fire-trigger/" + Uri.EscapeDataString(id);

    public static MtionTrigger ParseTrigger(JsonElement e) => new(
        e.Str("id"), e.Str("name"),
        e.TryGetProperty("output_parameters", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray().Select(p => new MtionParameter((int)(p.Num("parameter_index") ?? 0), p.Str("name"), p.Str("data_type"))).ToArray()
            : []);

    public static IReadOnlyList<MtionTrigger> ParseTriggers(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(ParseTrigger).ToArray() : [];

    public static MtionClubhouse ParseClubhouse(JsonElement e) => new(
        e.Str("id"), e.Str("name"), e.Str("description"),
        e.TryGetProperty("external_trigger_datas", out var ts) ? ParseTriggers(ts) : []);

    public static IReadOnlyList<MtionClubhouse> ParseClubhouses(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).Select(ParseClubhouse).ToArray() : [];

    /// <summary>A trigger by id, else by name (case-insensitive).</summary>
    public static MtionTrigger? Find(IEnumerable<MtionTrigger> triggers, string idOrName)
    {
        var key = idOrName.Trim();
        var list = triggers as IReadOnlyCollection<MtionTrigger> ?? triggers.ToArray();
        return list.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fire-trigger body: each value keyed by parameter name (or index), converted to the parameter's data type.
    /// Parameters not given are left out (mtion uses their defaults).
    /// </summary>
    public static string FireBody(MtionTrigger trigger, IReadOnlyDictionary<string, string> values)
    {
        var items = new List<Dictionary<string, object?>>();
        foreach (var (key, text) in values)
        {
            var parameter = trigger.Parameters.FirstOrDefault(p => string.Equals(p.Name, key.Trim(), StringComparison.OrdinalIgnoreCase))
                            ?? (int.TryParse(key.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                                ? trigger.Parameters.FirstOrDefault(p => p.Index == i)
                                : null)
                            ?? throw new ArgumentException($"mtion trigger '{trigger.Name}' has no parameter '{key}' (it has: {Names(trigger)}).");
            items.Add(new() { ["parameter_index"] = parameter.Index, ["value"] = Convert(parameter, text) });
        }
        return JsonSerializer.Serialize(items.OrderBy(d => (int)d["parameter_index"]!));
    }

    private static string Names(MtionTrigger t) => t.Parameters.Count == 0 ? "none" : string.Join(", ", t.Parameters.Select(p => $"{p.Name} ({p.DataType})"));

    internal static object? Convert(MtionParameter p, string text)
    {
        var t = text.Trim();
        switch (p.DataType.ToLowerInvariant())
        {
            case "number":
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d : throw new ArgumentException($"mtion parameter '{p.Name}' needs a number (got '{text}').");
            case "bool":
                return t.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => true,
                    "false" or "0" or "no" or "off" or "" => false,
                    _ => throw new ArgumentException($"mtion parameter '{p.Name}' needs true or false (got '{text}')."),
                };
            case "enum":
                return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)
                    ? e : throw new ArgumentException($"mtion parameter '{p.Name}' is an enum: give the option's number, 0 for the first (got '{text}').");
            default:
                return text;
        }
    }
}
