using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Elgato.Lights;

/// <summary>One light's state as the light reports it (GET /elgato/lights). Key Lights use temperature; Light Strips hue/saturation.</summary>
public sealed record LightState(bool On, int Brightness, int? Temperature, double? Hue, double? Saturation)
{
    /// <summary>The colour temperature in Kelvin (rounded to 50 K like Control Center), when the light reports one.</summary>
    public int? Kelvin => Temperature is { } mired ? ElgatoColor.MiredToKelvin(mired) : null;
}

/// <summary>GET /elgato/accessory-info.</summary>
public sealed record AccessoryInfo(
    string ProductName,
    string SerialNumber,
    string DisplayName,
    string FirmwareVersion,
    int FirmwareBuildNumber,
    int HardwareBoardType,
    IReadOnlyList<string> Features,
    string MacAddress);

/// <summary>Fields to change in a PUT /elgato/lights; null fields are left out of the request.</summary>
public sealed record LightChange(bool? On = null, int? Brightness = null, int? Temperature = null, double? Hue = null, double? Saturation = null);

/// <summary>Key Light temperature units: the API uses mireds 143-344 (about 7000-2900 K).</summary>
public static class ElgatoColor
{
    public const int MinMired = 143;
    public const int MaxMired = 344;
    public const int MinKelvin = 2900;
    public const int MaxKelvin = 7000;

    public static int MiredToKelvin(int mired) =>
        mired <= 0 ? 0 : (int)Math.Round(1_000_000.0 / mired / 50.0, MidpointRounding.AwayFromZero) * 50;

    public static int KelvinToMired(int kelvin) =>
        Math.Clamp((int)Math.Round(1_000_000.0 / Math.Clamp(kelvin, MinKelvin, MaxKelvin), MidpointRounding.AwayFromZero), MinMired, MaxMired);

    /// <summary>Kelvin (1000 and up) or raw API units (143-344) to API units.</summary>
    public static int ToMired(double value) => value switch
    {
        >= 1000 => KelvinToMired((int)Math.Round(value)),
        >= MinMired and <= MaxMired => (int)Math.Round(value),
        _ => throw new ArgumentException($"Temperature must be in Kelvin ({MinKelvin}-{MaxKelvin}) or API units ({MinMired}-{MaxMired}); got {value}."),
    };
}

/// <summary>
/// The lights' local HTTP API on port 9123 (Key Light, Key Light Air/Mini, Ring Light, Light Strip...). Community-documented;
/// see PROTOCOL.md. No authentication, JSON bodies, one request per call.
/// </summary>
internal sealed class ElgatoLightClient(HttpClient http)
{
    public static Uri BaseUri(string address, int port)
    {
        var host = address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;
        return new Uri($"http://{host}:{port}/");
    }

    public async Task<IReadOnlyList<LightState>> GetLightsAsync(Uri baseUri, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(new Uri(baseUri, "elgato/lights"), ct).ConfigureAwait(false);
        return ParseLights(doc.RootElement);
    }

    public async Task<IReadOnlyList<LightState>> PutLightsAsync(Uri baseUri, int count, LightChange change, CancellationToken ct)
    {
        var light = new JsonObject();
        if (change.On is { } on) light["on"] = on ? 1 : 0;
        if (change.Brightness is { } brightness) light["brightness"] = Math.Clamp(brightness, 0, 100);
        if (change.Temperature is { } temperature) light["temperature"] = temperature;
        if (change.Hue is { } hue) light["hue"] = Math.Clamp(hue, 0, 360);
        if (change.Saturation is { } saturation) light["saturation"] = Math.Clamp(saturation, 0, 100);
        var lights = new JsonArray();
        for (var i = 0; i < Math.Max(1, count); i++) lights.Add(light.DeepClone());
        var body = new JsonObject { ["numberOfLights"] = lights.Count, ["lights"] = lights };
        using var response = await SendAsync(HttpMethod.Put, new Uri(baseUri, "elgato/lights"), body.ToJsonString(), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Trim().Length == 0) return await GetLightsAsync(baseUri, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        return ParseLights(doc.RootElement);
    }

    public async Task<AccessoryInfo> GetAccessoryInfoAsync(Uri baseUri, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(new Uri(baseUri, "elgato/accessory-info"), ct).ConfigureAwait(false);
        return ParseAccessoryInfo(doc.RootElement);
    }

    /// <summary>The power-on/transition settings as compact JSON, or null when the light does not offer them.</summary>
    public async Task<string?> GetSettingsAsync(Uri baseUri, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(baseUri, "elgato/lights/settings"), ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonNode.Parse(text)?.ToJsonString();
    }

    public async Task SetDisplayNameAsync(Uri baseUri, string name, CancellationToken ct)
    {
        var body = new JsonObject { ["displayName"] = name }.ToJsonString();
        using var _ = await SendAsync(HttpMethod.Put, new Uri(baseUri, "elgato/accessory-info"), body, ct).ConfigureAwait(false);
    }

    /// <summary>Makes the light blink a few times.</summary>
    public async Task IdentifyAsync(Uri baseUri, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, new Uri(baseUri, "elgato/identify"), null, ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<LightState> ParseLights(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("lights", out var lights) || lights.ValueKind != JsonValueKind.Array)
            throw new FormatException("The light's answer has no \"lights\" array.");
        return lights.EnumerateArray().Select(l => new LightState(
            On: Num(l, "on") is { } on && on != 0,
            Brightness: (int)Math.Round(Num(l, "brightness") ?? 0),
            Temperature: Num(l, "temperature") is { } t ? (int)Math.Round(t) : null,
            Hue: Num(l, "hue"),
            Saturation: Num(l, "saturation"))).ToArray();
    }

    internal static AccessoryInfo ParseAccessoryInfo(JsonElement e)
    {
        var mac = e.TryGetProperty("wifi-info", out var wifi) && wifi.ValueKind == JsonValueKind.Object ? Str(wifi, "macAddress") : Str(e, "macAddress");
        var features = e.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.ToString()).ToArray()
            : [];
        return new AccessoryInfo(Str(e, "productName"), Str(e, "serialNumber"), Str(e, "displayName"), Str(e, "firmwareVersion"),
            (int)(Num(e, "firmwareBuildNumber") ?? 0), (int)(Num(e, "hardwareBoardType") ?? 0), features, mac);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Content = json is null ? new ByteArrayContent([]) : new StringContent(json, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The light answered HTTP {status} to {method} {uri.AbsolutePath}.");
        }
        return response;
    }

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
                _ => null,
            }
            : null;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
}
