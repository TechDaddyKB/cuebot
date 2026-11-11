using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cuebot.Donations.Streamlabs;

public sealed class StreamlabsApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Tokens from <c>POST /token</c>. Streamlabs says access tokens do not expire.</summary>
public sealed record StreamlabsTokens(string AccessToken, string RefreshToken);

/// <summary>The account behind an access token (<c>GET /user</c>).</summary>
/// <param name="Linked">Linked platforms, e.g. ("Twitch", "name"), in the order Streamlabs lists them.</param>
public sealed record StreamlabsUser(string Id, string DisplayName, IReadOnlyList<(string Platform, string Name)> Linked)
{
    public string LinkedText => string.Join(", ", Linked.Select(l => $"{l.Platform}: {l.Name}"));

    /// <summary>The linked Twitch login (<c>twitch.name</c>), the likely <c>channel</c> of the points calls.</summary>
    public string TwitchLogin { get; init; } = "";
}

/// <summary>One row of <c>GET /donations</c>.</summary>
public sealed record StreamlabsDonation(string Id, string Name, double Amount, string Currency, string Message, string CreatedAt)
{
    public string FormattedAmount => Money.Format(Amount, Currency);
}

public sealed record StreamlabsAlertProfile(string Id, string Name, bool Active);

public sealed record StreamlabsPointsRow(string UserName, long Points);

/// <summary>
/// The Streamlabs REST API v2.0 (https://streamlabs.com/api/v2.0, https://dev.streamlabs.com/reference): OAuth2
/// authorization-code sign-in with the user's own app, and the calls behind the Streamlabs steps. The access token goes
/// only in <c>Authorization: Bearer</c> (v2.0 refuses it as a query parameter). Secrets never appear in exceptions.
/// </summary>
internal sealed class StreamlabsApi(HttpClient http, Uri? baseUri = null)
{
    public static readonly Uri Production = new("https://streamlabs.com/api/v2.0/");

    /// <summary>
    /// Every scope the Streamlabs steps use (https://dev.streamlabs.com/docs/scopes). <c>legacy.token</c> is left out:
    /// it is restricted and needs Streamlabs' approval by email.
    /// </summary>
    public static readonly string[] Scopes =
    [
        "socket.token", "donations.read", "donations.create", "alerts.create", "alerts.write", "profiles.write",
        "credits.write", "jar.write", "wheel.write", "points.read", "points.write", "mediashare.control",
    ];

    /// <summary>The alert queue commands of <c>POST /alerts/{command}</c>.</summary>
    public static readonly string[] AlertCommands = ["skip", "mute_volume", "unmute_volume", "pause_queue", "unpause_queue", "show_video", "hide_video"];

    /// <summary>The media share commands (<c>/media-share/{command}</c>).</summary>
    public static readonly string[] MediaShareCommands =
    [
        "play-media", "pause-media", "skip-video", "playback-video", "volume-up", "volume-down",
        "enable-requests", "disable-requests", "enable-autoplay", "disable-autoplay", "enable-autoshow", "disable-autoshow",
        "enable-moderation", "disable-moderation",
    ];

    private readonly Uri _base = baseUri ?? Production;

    public Uri AuthorizeUrl(string clientId, string redirectUri, string state) => new(_base,
        "authorize?response_type=code" +
        $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}&state={Uri.EscapeDataString(state)}");

    public Task<StreamlabsTokens> ExchangeCodeAsync(string clientId, string clientSecret, string redirectUri, string code, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
        }, [clientSecret, code], null, ct);

    public Task<StreamlabsTokens> RefreshAsync(string clientId, string clientSecret, string redirectUri, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["refresh_token"] = refreshToken,
        }, [clientSecret, refreshToken], refreshToken, ct);

    /// <summary><c>GET /socket/token</c> → <c>{"socket_token":"…"}</c> (scope socket.token).</summary>
    public async Task<string> GetSocketTokenAsync(string accessToken, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "socket/token", accessToken, null, ct).ConfigureAwait(false);
        return json.Str("socket_token") is { Length: > 0 } token
            ? token
            : throw new StreamlabsApiException("Streamlabs did not return a socket token (is the socket.token scope granted?)");
    }

    /// <summary><c>GET /user</c> → <c>{"streamlabs":{id, display_name}, "twitch":{…}, "youtube":{id, title}, …}</c>.</summary>
    public async Task<StreamlabsUser> GetUserAsync(string accessToken, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "user", accessToken, null, ct).ConfigureAwait(false);
        var own = json.Child("streamlabs");
        var linked = new List<(string, string)>();
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in json.EnumerateObject())
            {
                if (property.Name == "streamlabs" || property.Value.ValueKind != JsonValueKind.Object) continue;
                var name = property.Value.First("display_name", "name", "title", "username");
                if (name.Length == 0) continue;
                linked.Add((StreamlabsEventMapper.Platform(property.Name + "_account"), name));
            }
        }
        var display = own.Str("display_name") is { Length: > 0 } d ? d : linked.Select(l => l.Item2).FirstOrDefault() ?? "";
        var twitch = json.Child("twitch");
        return new StreamlabsUser(own.Str("id"), display, linked) { TwitchLogin = twitch.Str("name") is { Length: > 0 } n ? n : twitch.Str("display_name") };
    }

    /// <summary>
    /// <c>GET /donations?limit&amp;after&amp;before&amp;currency</c> → <c>{"data":[{donation_id, created_at, currency, amount, name,
    /// message}]}</c>, newest first. <paramref name="after"/>/<paramref name="before"/> are donation ids (cursor paging).
    /// </summary>
    public async Task<IReadOnlyList<StreamlabsDonation>> GetDonationsAsync(string accessToken, int limit, string? after, string? before,
        string? currency, CancellationToken ct)
    {
        var query = new StringBuilder($"donations?limit={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(after)) query.Append("&after=").Append(Uri.EscapeDataString(after.Trim()));
        if (!string.IsNullOrWhiteSpace(before)) query.Append("&before=").Append(Uri.EscapeDataString(before.Trim()));
        if (!string.IsNullOrWhiteSpace(currency)) query.Append("&currency=").Append(Uri.EscapeDataString(currency.Trim().ToUpperInvariant()));
        var json = await SendAsync(HttpMethod.Get, query.ToString(), accessToken, null, ct).ConfigureAwait(false);
        return Rows(json, "data").Select(Donation).Where(d => d.Id.Length > 0).ToArray();
    }

    internal static StreamlabsDonation Donation(JsonElement row) => new(
        row.First("donation_id", "id"), row.First("name", "from") is { Length: > 0 } n ? n : "Anonymous",
        Math.Round(row.Number("amount"), 2), row.Str("currency").ToUpperInvariant(), row.Str("message"), CreatedAt(row.Str("created_at")));

    /// <summary>Streamlabs sends Unix seconds ("1438576556"); turned into ISO 8601.</summary>
    private static string CreatedAt(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O", CultureInfo.InvariantCulture)
            : value;

    /// <summary>
    /// <c>POST /donations</c> (scope donations.create): records a donation (e.g. one received offline). Name 2-25
    /// characters and message under 255 are checked here. Returns the new donation id when Streamlabs says it.
    /// </summary>
    public async Task<string> CreateDonationAsync(string accessToken, string name, string identifier, double amount, string currency,
        string message, bool skipAlert, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 25) throw new StreamlabsApiException("The donor name must be 2 to 25 characters long");
        if (message.Length >= 255) throw new StreamlabsApiException("The message must be shorter than 255 characters");
        if (amount <= 0) throw new StreamlabsApiException("The amount must be more than 0");
        if (currency.Trim().Length != 3) throw new StreamlabsApiException("The currency must be a 3-letter code such as USD");
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["identifier"] = identifier.Trim().Length > 0 ? identifier.Trim() : name,
            ["amount"] = amount,
            ["currency"] = currency.Trim().ToUpperInvariant(),
            ["message"] = message,
            ["skip_alert"] = skipAlert ? "yes" : "no",
        };
        var json = await SendAsync(HttpMethod.Post, "donations", accessToken, body, ct).ConfigureAwait(false);
        return json.First("donation_id", "id");
    }

    /// <summary><c>POST /alerts</c> (scope alerts.create): shows a custom alert in the Streamlabs alert box.</summary>
    public Task CreateAlertAsync(string accessToken, string type, string message, string userMessage, string imageHref, string soundHref,
        int durationMs, string specialTextColor, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["type"] = type.Trim().ToLowerInvariant() };
        void Add(string key, string value)
        {
            if (value.Trim().Length > 0) body[key] = value.Trim();
        }
        Add("message", message);
        Add("user_message", userMessage);
        Add("image_href", imageHref);
        Add("sound_href", soundHref);
        Add("special_text_color", specialTextColor);
        if (durationMs > 0) body["duration"] = durationMs.ToString(CultureInfo.InvariantCulture);
        return SendAsync(HttpMethod.Post, "alerts", accessToken, body, ct);
    }

    /// <summary><c>POST /alerts/{command}</c> (scope alerts.write): skip, mute/unmute, pause/unpause the queue, show/hide video.</summary>
    public Task AlertCommandAsync(string accessToken, string command, CancellationToken ct)
    {
        command = command.Trim().ToLowerInvariant();
        if (!AlertCommands.Contains(command)) throw new StreamlabsApiException($"Unknown alert command '{command}'");
        return SendAsync(HttpMethod.Post, $"alerts/{command}", accessToken, new Dictionary<string, object?>(), ct);
    }

    /// <summary><c>GET /alerts/get_status</c> → <c>{"paused": bool}</c>.</summary>
    public async Task<bool> GetAlertQueuePausedAsync(string accessToken, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "alerts/get_status", accessToken, null, ct).ConfigureAwait(false);
        return json.Child("paused") is { } paused && paused.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => paused.GetDouble() != 0,
            JsonValueKind.String => paused.GetString() is "true" or "1",
            _ => false,
        };
    }

    /// <summary><c>POST /alerts/send_test_alert</c> {type, platform} (scope alerts.write).</summary>
    public Task SendTestAlertAsync(string accessToken, string type, string platform, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "alerts/send_test_alert", accessToken, new Dictionary<string, object?>
        {
            ["type"] = type.Trim().ToLowerInvariant(),
            ["platform"] = platform.Trim().ToLowerInvariant(),
        }, ct);

    /// <summary>
    /// <c>GET /alert_profiles/get</c> (scope profiles.write). The response has no documented example; a bare array or
    /// <c>{data|profiles: [{id, name, active}]}</c> is read.
    /// </summary>
    public async Task<IReadOnlyList<StreamlabsAlertProfile>> GetAlertProfilesAsync(string accessToken, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "alert_profiles/get", accessToken, null, ct).ConfigureAwait(false);
        var rows = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : Rows(json, "data", "profiles");
        return rows.Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => new StreamlabsAlertProfile(r.First("id", "_id"), r.First("name", "title"),
                r.Child("active") is { ValueKind: JsonValueKind.True } || r.Child("is_active") is { ValueKind: JsonValueKind.True }))
            .Where(p => p.Id.Length > 0)
            .ToArray();
    }

    /// <summary><c>POST /alert_profiles/activate</c> {id} (scope profiles.write).</summary>
    public Task ActivateAlertProfileAsync(string accessToken, string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "alert_profiles/activate", accessToken, new Dictionary<string, object?> { ["id"] = id.Trim() }, ct);

    /// <summary><c>POST /credits/roll</c> (credits.write; Streamlabs answers 422 when you are not live).</summary>
    public Task RollCreditsAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "credits/roll", accessToken, new Dictionary<string, object?>(), ct);

    /// <summary><c>POST /jar/empty</c> (jar.write).</summary>
    public Task EmptyJarAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "jar/empty", accessToken, new Dictionary<string, object?>(), ct);

    /// <summary><c>POST /wheel/spin</c> (wheel.write).</summary>
    public Task SpinWheelAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "wheel/spin", accessToken, new Dictionary<string, object?>(), ct);

    // ---- Cloudbot loyalty points (points.read / points.write) ----

    /// <summary><c>GET /points?username&amp;channel</c> → <c>{username, points, …}</c>.</summary>
    public async Task<long> GetPointsAsync(string accessToken, string user, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"points?username={Q(user)}&channel={Q(channel)}", accessToken, null, ct).ConfigureAwait(false);
        return (long)json.Number("points");
    }

    /// <summary><c>POST /points/subtract</c> {username, channel, points} → the user's record with the new total.</summary>
    public async Task<long> SubtractPointsAsync(string accessToken, string user, string channel, long points, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "points/subtract", accessToken, new Dictionary<string, object?>
        {
            ["username"] = user.Trim(), ["channel"] = channel.Trim(), ["points"] = points,
        }, ct).ConfigureAwait(false);
        return (long)json.Number("points");
    }

    /// <summary><c>POST /points/import</c> {channel, users: {name: points}}: adds points to each user.</summary>
    public Task ImportPointsAsync(string accessToken, string channel, IReadOnlyDictionary<string, long> users, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "points/import", accessToken, new Dictionary<string, object?>
        {
            ["channel"] = channel.Trim(), ["users"] = users,
        }, ct);

    /// <summary><c>POST /points/user_point_edit</c> {username, points}: sets a user's points.</summary>
    public async Task<long> SetPointsAsync(string accessToken, string user, string channel, long points, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "points/user_point_edit", accessToken, new Dictionary<string, object?>
        {
            ["username"] = user.Trim(), ["channel"] = channel.Trim(), ["points"] = points,
        }, ct).ConfigureAwait(false);
        return json.Has("points") ? (long)json.Number("points") : points;
    }

    /// <summary><c>POST /points/add_to_all</c> {channel, value}: gives everyone in the channel points.</summary>
    public Task AddPointsToAllAsync(string accessToken, string channel, long value, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "points/add_to_all", accessToken, new Dictionary<string, object?>
        {
            ["channel"] = channel.Trim(), ["value"] = value,
        }, ct);

    /// <summary><c>POST /points/reset</c>: sets everyone's points to 0.</summary>
    public Task ResetPointsAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "points/reset", accessToken, new Dictionary<string, object?>(), ct);

    /// <summary><c>GET /points/user_points?limit&amp;page&amp;sort=points&amp;order=desc</c>: the points leaderboard.</summary>
    public async Task<IReadOnlyList<StreamlabsPointsRow>> GetPointsLeaderboardAsync(string accessToken, int limit, CancellationToken ct)
    {
        var path = $"points/user_points?limit={Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture)}&page=1&sort=points&order=desc";
        var json = await SendAsync(HttpMethod.Get, path, accessToken, null, ct).ConfigureAwait(false);
        var rows = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : Rows(json, "data", "users", "points");
        return rows.Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => new StreamlabsPointsRow(r.First("username", "name"), (long)r.Number("points")))
            .OrderByDescending(r => r.Points)
            .Take(Math.Clamp(limit, 1, 100))
            .ToArray();
    }

    // ---- Media share (mediashare.control) ----

    /// <summary>
    /// <c>/media-share/{command}</c>. The scopes page lists PUT for every command while the reference shows PATCH for
    /// the enable/disable ones: the documented verb is tried first and the other one when Streamlabs answers 404/405.
    /// </summary>
    public async Task MediaShareAsync(string accessToken, string command, int? volume, CancellationToken ct)
    {
        command = command.Trim().ToLowerInvariant();
        if (!MediaShareCommands.Contains(command)) throw new StreamlabsApiException($"Unknown media share command '{command}'");
        var body = new Dictionary<string, object?>();
        if (volume is { } v) body["volume"] = Math.Clamp(v, 0, 100);
        var first = command.StartsWith("enable-", StringComparison.Ordinal) || command.StartsWith("disable-", StringComparison.Ordinal)
            ? HttpMethod.Patch : HttpMethod.Put;
        try
        {
            await SendAsync(first, $"media-share/{command}", accessToken, body, ct).ConfigureAwait(false);
        }
        catch (StreamlabsApiException ex) when (ex.Status is 404 or 405)
        {
            await SendAsync(first == HttpMethod.Put ? HttpMethod.Patch : HttpMethod.Put, $"media-share/{command}", accessToken, body, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>GET /media-share/settings</c> (undocumented response; returned as JSON text).</summary>
    public async Task<JsonElement> GetMediaShareSettingsAsync(string accessToken, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, "media-share/settings", accessToken, null, ct).ConfigureAwait(false);

    // ---- plumbing ----

    private static string Q(string value) => Uri.EscapeDataString(value.Trim());

    private static JsonElement[] Rows(JsonElement json, params string[] names)
    {
        foreach (var name in names)
            if (json.Child(name) is { ValueKind: JsonValueKind.Array } array) return array.EnumerateArray().ToArray();
        return [];
    }

    /// <summary>Standard OAuth2 form post to <c>/token</c>; the response is <c>{access_token, token_type, refresh_token}</c>.</summary>
    private async Task<StreamlabsTokens> TokenAsync(Dictionary<string, string> fields, string[] secrets, string? previousRefresh, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "token")) { Content = new FormUrlEncodedContent(fields) };
        AddHeaders(request);
        var (status, json) = await ReadAsync(request, ct).ConfigureAwait(false);
        var access = json.Str("access_token");
        if (status is < 200 or > 299 || access.Length == 0)
        {
            var detail = json.First("error_description", "message", "error");
            var text = status is 400 or 401 ? "Streamlabs refused the sign-in" : $"Streamlabs token error {status.ToString(CultureInfo.InvariantCulture)}";
            if (detail.Length > 0) text += $": {detail}";
            foreach (var secret in secrets) text = Secrets.Redact(text, secret);
            throw new StreamlabsApiException(text, status is >= 200 and <= 299 ? 400 : status);
        }
        var refresh = json.Str("refresh_token");
        return new StreamlabsTokens(access, refresh.Length > 0 ? refresh : previousRefresh ?? "");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string accessToken, Dictionary<string, object?>? body, CancellationToken ct)
    {
        if (accessToken.Trim().Length == 0) throw new StreamlabsApiException("Connect Cuebot to your Streamlabs app first (Donations → Streamlabs)", 401);
        using var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        AddHeaders(request);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var (status, json) = await ReadAsync(request, ct).ConfigureAwait(false);
        if (status is >= 200 and <= 299) return json;
        var detail = json.First("message", "error_description", "error");
        if (detail is "true" or "false") detail = "";
        var text = status switch
        {
            401 => "Streamlabs rejected the access token (connect again)",
            403 => "Streamlabs refused the request (the app may lack the scope; connect again)",
            429 => "Streamlabs rate limit reached (an unapproved app may make 5 requests a minute)",
            _ => $"Streamlabs API error {status.ToString(CultureInfo.InvariantCulture)}",
        };
        if (detail.Length > 0) text += $": {detail}";
        throw new StreamlabsApiException(Secrets.Redact(text, accessToken), status);
    }

    private static void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Cuebot");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The documented samples send it; harmless otherwise.
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
    }

    private async Task<(int Status, JsonElement Json)> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonReaders.TryParse(text.Trim().Length == 0 ? "{}" : text) ?? JsonReaders.TryParse("{}")!.Value;
        return ((int)response.StatusCode, json);
    }
}

/// <summary>
/// A sliding-window limit of requests per minute (Streamlabs: 5 for an unapproved app). A call over the limit waits for
/// a free slot.
/// </summary>
internal sealed class RequestRateLimiter(Func<int> perMinute, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long the last call had to wait (for tests and the log).</summary>
    public TimeSpan LastWait { get; private set; }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var limit = Math.Max(1, perMinute());
            var now = time.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek() >= Window) _sent.Dequeue();
            LastWait = TimeSpan.Zero;
            if (_sent.Count >= limit)
            {
                var wait = _sent.Peek() + Window - now;
                LastWait = wait;
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
                _sent.Dequeue();
            }
            _sent.Enqueue(time.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }
    }
}
