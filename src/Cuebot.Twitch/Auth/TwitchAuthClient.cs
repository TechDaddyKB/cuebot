using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cuebot.Twitch.Auth;

/// <summary>
/// Twitch OAuth for public clients: Device Code Grant Flow, refresh, validation and revocation.
/// No client secret is ever used.
/// </summary>
internal sealed class TwitchAuthClient(HttpClient http, Uri authBase, TimeProvider time)
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    public async Task<DeviceAuthorization> StartDeviceFlowAsync(string clientId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        using var response = await PostFormAsync("device", new()
        {
            ["client_id"] = clientId,
            ["scopes"] = string.Join(' ', scopes),
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TwitchApiException($"Twitch refused to start sign-in: {ErrorMessage(body)}", (int)response.StatusCode);

        return new DeviceAuthorization(
            body.Str("user_code"),
            body.Str("verification_uri"),
            time.GetUtcNow().AddSeconds(body.Int("expires_in")))
        {
            ClientId = clientId,
            DeviceCode = body.Str("device_code"),
            Scopes = scopes,
            PollInterval = TimeSpan.FromSeconds(Math.Max(1, body.Int("interval"))),
        };
    }

    /// <summary>Polls the token endpoint until the user approves, denies, or the code expires.</summary>
    public async Task<TwitchTokens> PollDeviceTokenAsync(DeviceAuthorization auth, CancellationToken ct)
    {
        var interval = auth.PollInterval;
        while (true)
        {
            if (time.GetUtcNow() >= auth.ExpiresAt)
                throw new TwitchApiException("The Twitch sign-in code expired. Start sign-in again.");

            await Task.Delay(interval, time, ct).ConfigureAwait(false);
            using var response = await PostFormAsync("token", new()
            {
                ["client_id"] = auth.ClientId,
                ["scopes"] = string.Join(' ', auth.Scopes),
                ["device_code"] = auth.DeviceCode,
                ["grant_type"] = DeviceGrantType,
            }, ct).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return ParseTokens(body);

            switch (ErrorMessage(body))
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += SlowDownStep;
                    continue;
                case var error:
                    throw new TwitchApiException($"Twitch sign-in failed: {error}", (int)response.StatusCode);
            }
        }
    }

    public async Task<TwitchTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        using var response = await PostFormAsync("token", new()
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? ParseTokens(body)
            : throw new TwitchApiException($"Refreshing the Twitch token failed: {ErrorMessage(body)}", (int)response.StatusCode);
    }

    /// <summary>Returns null when Twitch says the token is invalid or expired.</summary>
    public async Task<TokenValidation?> ValidateAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(authBase, "validate"));
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        var body = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TwitchApiException($"Validating the Twitch token failed: {ErrorMessage(body)}", (int)response.StatusCode);

        return new TokenValidation(body.Str("client_id"), body.Str("login"), body.Str("user_id"),
            body.Strings("scopes"), body.Int("expires_in"));
    }

    public async Task RevokeAsync(string clientId, string accessToken, CancellationToken ct)
    {
        using var response = await PostFormAsync("revoke", new()
        {
            ["client_id"] = clientId,
            ["token"] = accessToken,
        }, ct).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
        http.PostAsync(new Uri(authBase, path), new FormUrlEncodedContent(form), ct);

    private static TwitchTokens ParseTokens(JsonElement body) =>
        new(body.Str("access_token"), body.Str("refresh_token"), body.Strings("scope"));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string ErrorMessage(JsonElement body)
    {
        var message = body.Str("message");
        return message.Length > 0 ? message : body.Str("error") is { Length: > 0 } error ? error : "unknown error";
    }
}
