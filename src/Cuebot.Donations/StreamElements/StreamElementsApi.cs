using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cuebot.Donations.StreamElements;

public sealed class StreamElementsApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record StreamElementsChannel(string Id, string Name, string DisplayName);

public sealed record StreamElementsPoints(string UserName, long Points, long PointsAllTime, int Rank);

public sealed record StreamElementsTip(string Id, string UserName, double Amount, string Currency, string Message, string CreatedAt)
{
    public string FormattedAmount => Money.Format(Amount, Currency);
}

public sealed record StreamElementsRank(string UserName, double Total);

/// <summary>
/// The StreamElements REST API (https://api.streamelements.com/kappa/v2), authenticated with the channel's JWT
/// (<c>Authorization: Bearer &lt;jwt&gt;</c>). The token never appears in exceptions.
/// </summary>
internal sealed class StreamElementsApi(HttpClient http, Uri? baseUri = null)
{
    public static readonly Uri Production = new("https://api.streamelements.com/kappa/v2/");

    private readonly Uri _base = baseUri ?? Production;

    public async Task<StreamElementsChannel> GetMyChannelAsync(string jwt, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "channels/me", jwt, ct).ConfigureAwait(false);
        var id = json.Str("_id");
        if (id.Length == 0) throw new StreamElementsApiException("StreamElements did not return a channel for this token");
        return new StreamElementsChannel(id, json.Str("username"), json.First("displayName", "alias", "username"));
    }

    public async Task<StreamElementsPoints> GetUserPointsAsync(string jwt, string channel, string user, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"points/{Seg(channel)}/{Seg(user)}", jwt, ct).ConfigureAwait(false);
        return new StreamElementsPoints(json.Str("username") is { Length: > 0 } name ? name : user,
            (long)json.Number("points"), (long)json.Number("pointsAlltime"), (int)json.Number("rank"));
    }

    /// <summary>Adds (or, with a negative amount, removes) points; returns the user's new total.</summary>
    public async Task<long> AddUserPointsAsync(string jwt, string channel, string user, long amount, CancellationToken ct)
    {
        var path = $"points/{Seg(channel)}/{Seg(user)}/{amount.ToString(CultureInfo.InvariantCulture)}";
        var json = await SendAsync(HttpMethod.Put, path, jwt, ct).ConfigureAwait(false);
        return (long)json.Number("newAmount");
    }

    public async Task<IReadOnlyList<StreamElementsRank>> GetPointsLeaderboardAsync(string jwt, string channel, int limit, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"points/{Seg(channel)}/top?limit={Clamp(limit)}", jwt, ct).ConfigureAwait(false);
        return json.Items("users").Select(u => new StreamElementsRank(u.Str("username"), u.Number("points"))).ToArray();
    }

    public async Task<IReadOnlyList<StreamElementsTip>> GetTipsAsync(string jwt, string channel, int limit, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"tips/{Seg(channel)}?limit={Clamp(limit)}&sort=-createdAt", jwt, ct).ConfigureAwait(false);
        return json.Items("docs").Select(Tip).ToArray();
    }

    /// <summary>Top tippers (UNVERIFIED endpoint shape: accepts an array or an object with docs/users).</summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetTipLeaderboardAsync(string jwt, string channel, int limit, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"tips/{Seg(channel)}/leaderboard?limit={Clamp(limit)}", jwt, ct).ConfigureAwait(false);
        var rows = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray()
            : json.Items("docs").Concat(json.Items("users")).Concat(json.Items("leaderboard"));
        return rows.Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => new StreamElementsRank(r.First("username", "_id", "name"), Math.Round(r.Number(r.Has("total") ? "total" : "amount"), 2)))
            .Take(Clamp(limit))
            .ToArray();
    }

    internal static StreamElementsTip Tip(JsonElement doc)
    {
        var donation = doc.Child("donation") ?? default;
        var user = donation.ValueKind == JsonValueKind.Object ? donation.Child("user") ?? default : default;
        return new StreamElementsTip(doc.Str("_id"),
            user.ValueKind == JsonValueKind.Object ? user.Str("username") : "",
            donation.ValueKind == JsonValueKind.Object ? donation.Number("amount") : 0,
            donation.ValueKind == JsonValueKind.Object ? donation.Str("currency") : "",
            donation.ValueKind == JsonValueKind.Object ? donation.Str("message") : "",
            doc.Str("createdAt"));
    }

    private static int Clamp(int limit) => Math.Clamp(limit, 1, 100);

    private static string Seg(string value) => Uri.EscapeDataString(value.Trim());

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string jwt, CancellationToken ct)
    {
        if (jwt.Trim().Length == 0) throw new StreamElementsApiException("Enter your StreamElements JWT token first");
        using var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonReaders.TryParse(body.Length == 0 ? "{}" : body);
        if (!response.IsSuccessStatusCode)
        {
            var detail = json is { ValueKind: JsonValueKind.Object } j ? j.First("message", "error") : "";
            var status = (int)response.StatusCode;
            var text = status == 401 ? "StreamElements rejected the JWT token" : $"StreamElements API error {status}";
            throw new StreamElementsApiException(Secrets.Redact(detail.Length > 0 ? $"{text}: {detail}" : text, jwt), status);
        }
        return json ?? throw new StreamElementsApiException("StreamElements returned an unreadable response");
    }
}
