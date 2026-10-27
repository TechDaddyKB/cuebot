using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cuebot.Webhooks;

/// <summary>Signature and token checks. All comparisons are constant-time.</summary>
public static class WebhookVerification
{
    /// <summary>Base64 HMAC-SHA256 of <paramref name="body"/> (Shopify, Fourthwall).</summary>
    public static string HmacSha256Base64(string secret, byte[] body) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Lower-case hex HMAC-MD5 of <paramref name="body"/> (Patreon).</summary>
    public static string HmacMd5Hex(string secret, byte[] body) =>
        Convert.ToHexStringLower(HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>True when the base64 <paramref name="header"/> is the HMAC-SHA256 of the body.</summary>
    public static bool CheckHmacSha256Base64(string secret, byte[] body, string header)
    {
        if (secret.Length == 0 || header.Trim().Length == 0) return false;
        byte[] given;
        try { given = Convert.FromBase64String(header.Trim()); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>True when the hex <paramref name="header"/> (any case) is the HMAC-MD5 of the body.</summary>
    public static bool CheckHmacMd5Hex(string secret, byte[] body, string header)
    {
        if (secret.Length == 0 || header.Trim().Length == 0) return false;
        byte[] given;
        try { given = Convert.FromHexString(header.Trim()); }
        catch (FormatException) { return false; }
        var expected = HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>Constant-time string equality (false when either side is empty).</summary>
    public static bool TokensEqual(string expected, string given)
    {
        if (expected.Length == 0 || given.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
    }

    /// <summary>A random URL-safe token for Throne and custom hooks (32 characters).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>SHA-256 of the body, hex; a dedupe key when a service sends no event id.</summary>
    internal static string BodyHash(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));
}

/// <summary>Keeps secrets out of logs, status text and the UI.</summary>
public static class WebhookSecrets
{
    /// <summary>"••••••••3f9a" for a set secret, "" for none. Shows at most the last four characters of long secrets.</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var tail = secret.Length >= 16 ? secret[^4..] : "";
        return new string('•', 8) + tail;
    }
}

/// <summary>Parses <c>a=1&amp;b=two</c> query strings and form bodies (later duplicates win).</summary>
internal static class FormData
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }
}

/// <summary>Lenient readers for service payloads: missing, null or oddly typed fields yield defaults.</summary>
internal static class JsonReaders
{
    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static JsonElement? Child(this JsonElement? element, string name) => element is { } e ? e.Child(name) : null;

    /// <summary>Follows a dotted path ("amounts.total.value").</summary>
    public static JsonElement? Path(this JsonElement element, string path)
    {
        JsonElement? current = element;
        foreach (var part in path.Split('.')) current = current.Child(part);
        return current;
    }

    public static string Str(this JsonElement element, string name) =>
        element.Child(name) is { } value ? Text(value) : "";

    public static string Str(this JsonElement? element, string name) => element is { } e ? e.Str(name) : "";

    public static string PathStr(this JsonElement element, string path) => element.Path(path) is { } v ? Text(v) : "";

    /// <summary>The first non-empty of several fields.</summary>
    public static string First(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.Str(name) is { Length: > 0 } value) return value;
        return "";
    }

    public static double Number(this JsonElement element, string name) => element.Child(name) is { } v ? Number(v) : 0;

    public static double PathNumber(this JsonElement element, string path) => element.Path(path) is { } v ? Number(v) : 0;

    public static bool Bool(this JsonElement element, string name) => element.Child(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out var b) && b,
        _ => false,
    };

    public static double Number(this JsonElement? element, string name) => element is { } e ? e.Number(name) : 0;

    public static JsonElement? Path(this JsonElement? element, string path) => element is { } e ? e.Path(path) : null;

    public static string PathStr(this JsonElement? element, string path) => element is { } e ? e.PathStr(path) : "";

    public static double PathNumber(this JsonElement? element, string path) => element is { } e ? e.PathNumber(path) : 0;

    public static bool Has(this JsonElement element, string name) => element.Child(name) is not null;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Child(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    public static double Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => ParseNumber(value.GetString()),
        _ => 0,
    };

    /// <summary>"13.37", "1,000.50" and "5" all parse; anything else is 0.</summary>
    public static double ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var cleaned = text.Trim().Replace(",", "", StringComparison.Ordinal);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>A JSON scalar as an argument value: string, long (integers, so big ids stay exact), double, bool or null.</summary>
    public static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => null,
    };

    /// <summary>ISO timestamps become <see cref="DateTimeOffset"/>; anything else stays text.</summary>
    public static object Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : text;

    /// <summary>
    /// Writes every scalar under <paramref name="element"/> as <c>prefix.key</c>, <c>prefix.list[0].key</c>
    /// (the way Streamer.bot names nested webhook fields). Objects deeper than <paramref name="maxDepth"/> are skipped.
    /// </summary>
    public static void Flatten(JsonElement element, string prefix, IDictionary<string, object?> into, int maxDepth = 6)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (maxDepth < 0) return;
                foreach (var p in element.EnumerateObject())
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", into, maxDepth - 1);
                break;
            case JsonValueKind.Array:
                if (maxDepth < 0) return;
                var i = 0;
                foreach (var item in element.EnumerateArray()) Flatten(item, $"{prefix}[{i++}]", into, maxDepth - 1);
                break;
            default:
                if (prefix.Length > 0) into[prefix] = Scalar(element);
                break;
        }
    }

    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a JSON body or throws <see cref="FormatException"/> (reported as Failed).</summary>
    public static JsonElement ParseBody(string text) =>
        TryParse(text) is { ValueKind: JsonValueKind.Object } root ? root : throw new FormatException("The body is not a JSON object");
}

/// <summary>Display amounts ("$5.00", "€12.50", "1,000 JPY").</summary>
internal static class Money
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥",
        ["CNY"] = "CN¥", ["INR"] = "₹", ["KRW"] = "₩", ["BRL"] = "R$", ["MXN"] = "MX$", ["RUB"] = "₽", ["TRY"] = "₺",
        ["PLN"] = "zł", ["CHF"] = "CHF ", ["SEK"] = "kr ", ["NOK"] = "kr ", ["DKK"] = "kr ", ["ILS"] = "₪", ["PHP"] = "₱",
    };

    private static readonly HashSet<string> NoDecimals = new(StringComparer.OrdinalIgnoreCase) { "JPY", "KRW" };

    public static string Format(double amount, string currency)
    {
        var code = currency.Trim().ToUpperInvariant();
        var number = amount.ToString(NoDecimals.Contains(code) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        if (code.Length == 0) return number;
        return Symbols.TryGetValue(code, out var symbol) ? symbol + number : $"{number} {code}";
    }

    /// <summary>True when <paramref name="currency"/> is in the comma/space separated <paramref name="allowed"/> list (empty allows all).</summary>
    public static bool CurrencyAllowed(string allowed, string currency)
    {
        var list = allowed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 || list.Any(c => string.Equals(c, currency, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Remembers the last few hundred event ids so a re-sent webhook is published once.</summary>
internal sealed class RecentIds(int capacity = 1000)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>
    /// True when none of the (non-empty) keys was seen before; then all of them are remembered. An event without
    /// keys is always new.
    /// </summary>
    public bool AddAll(IReadOnlyList<string> keys)
    {
        var real = keys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (real.Length == 0) return true;
        lock (_gate)
        {
            if (real.Any(_set.Contains)) return false;
            foreach (var key in real)
            {
                _set.Add(key);
                _order.Enqueue(key);
            }
            while (_order.Count > capacity) _set.Remove(_order.Dequeue());
            return true;
        }
    }

    public bool Contains(string key)
    {
        lock (_gate) return _set.Contains(key);
    }
}
