using System.Text.Json;

namespace Cuebot.Rumble.Auth;

/// <summary>
/// Performs Rumble's web login (unofficial, see PROTOCOL.md) through <c>service.php</c>: fetch the account's
/// salts, check whether two-factor sign-in is required, then log in with the salted hashes. The password is only
/// used to compute those hashes and is never stored or logged.
/// </summary>
internal sealed class RumbleLoginClient(HttpClient http, RumbleEndpoints endpoints)
{
    public async Task<RumbleSession> SignInAsync(string username, string password, CancellationToken ct)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0) throw new InvalidOperationException("Enter your Rumble username and password.");

        string hashes;
        using (var salts = await CallAsync("user.get_salts", [new("username", username)], ct).ConfigureAwait(false))
            hashes = PasswordHasher.Hash(password, salts.Data.Strings("salts"));

        using (var firstStep = await CallAsync("user.2fa.first_step",
                   [new("legacy_password", "1"), new("login", username), new("password", hashes), new("redirect_uri", endpoints.Site.ToString())],
                   ct).ConfigureAwait(false))
        {
            if (firstStep.Data.Str("totp_id").Length > 0)
                throw new RumbleApiException("This Rumble account uses two-factor sign-in, which Cuebot does not support yet.");
        }

        using var login = await CallAsync("user.login", [new("username", username), new("password_hashes", hashes)], ct).ConfigureAwait(false);
        var cookie = login.SessionCookie ?? (login.Data.Child("session") is { ValueKind: JsonValueKind.String } s ? s.GetString() : null);
        if (string.IsNullOrEmpty(cookie)) throw new RumbleApiException("Rumble rejected the username or password.");
        return new RumbleSession(cookie, username);
    }

    private async Task<ServiceResponse> CallAsync(string service, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.Service(service)) { Content = new FormUrlEncodedContent(form) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RumbleApiException($"Rumble's login answered HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            throw new RumbleApiException("Rumble's login answered with an unexpected format.");
        }

        var data = document.RootElement.Child("data") ?? default;
        if (data.Child("success") is { ValueKind: JsonValueKind.False })
        {
            document.Dispose();
            throw new RumbleApiException("Rumble rejected the username or password.");
        }
        return new ServiceResponse(document, data, SessionCookieOf(response));
    }

    private static string? SessionCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';', 2)[0].Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim() == RumbleSession.CookieName && pair[1].Length > 0)
                .Select(pair => pair[1].Trim())
                .LastOrDefault()
            : null;

    private sealed record ServiceResponse(JsonDocument Document, JsonElement Data, string? SessionCookie) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }
}
