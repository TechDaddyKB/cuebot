using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Auth;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.SignIn;

/// <summary>Where each provider's sign-in pages and APIs are. Tests point these at a fake server.</summary>
public sealed record PortalProviderEndpoints(
    string GoogleAuthorize, string GoogleToken, string GoogleUserInfo, string GoogleRevoke,
    string TwitchAuthorize, string TwitchToken, string TwitchValidate, string TwitchUsers, string TwitchRevoke,
    string DiscordAuthorize, string DiscordToken, string DiscordMe, string DiscordRevoke)
{
    public static PortalProviderEndpoints Default { get; } = new(
        GoogleAuthorize: "https://accounts.google.com/o/oauth2/v2/auth",
        GoogleToken: "https://oauth2.googleapis.com/token",
        GoogleUserInfo: "https://openidconnect.googleapis.com/v1/userinfo",
        GoogleRevoke: "https://oauth2.googleapis.com/revoke",
        TwitchAuthorize: "https://id.twitch.tv/oauth2/authorize",
        TwitchToken: "https://id.twitch.tv/oauth2/token",
        TwitchValidate: "https://id.twitch.tv/oauth2/validate",
        TwitchUsers: "https://api.twitch.tv/helix/users",
        TwitchRevoke: "https://id.twitch.tv/oauth2/revoke",
        DiscordAuthorize: "https://discord.com/oauth2/authorize",
        DiscordToken: "https://discord.com/api/oauth2/token",
        DiscordMe: "https://discord.com/api/users/@me",
        DiscordRevoke: "https://discord.com/api/oauth2/token/revoke");
}

/// <summary>The sign-in apps the portal offers, in build order (docs/MOD_PORTAL_PLAN.md, section 4).</summary>
public sealed record PortalProviderApps(AppCredentials Google, AppCredentials Twitch, AppCredentials Discord)
{
    public static PortalProviderApps BuiltIn { get; } = new(BuiltInApps.PortalGoogle, BuiltInApps.PortalTwitch, BuiltInApps.PortalDiscord);

    public AppCredentials For(IdentityProvider provider) => provider switch
    {
        IdentityProvider.Google => Google,
        IdentityProvider.Twitch => Twitch,
        IdentityProvider.Discord => Discord,
        _ => AppCredentials.None,
    };
}

/// <summary>Turns a sign-in code into the account behind it. One implementation per Cuebot; fakes in tests.</summary>
public interface IPortalIdentityExchange
{
    /// <summary>The providers a mod can sign in with, as the loader needs them to start a sign-in.</summary>
    JsonArray Describe();

    /// <summary>
    /// Exchanges the code (and the device's PKCE verifier) and reads who signed in. Throws
    /// <see cref="PortalAccessException"/> with words for the mod when the provider refuses.
    /// </summary>
    Task<ProviderIdentity> ExchangeAsync(IdentityProvider provider, string code, string verifier, CancellationToken ct);
}

/// <summary>
/// The code exchange with Google, Twitch and Discord. The device starts the sign-in with PKCE where the provider
/// supports it (Google, Discord) and returns through <see cref="RedirectUri"/>; Cuebot, which holds the client secret,
/// exchanges the code, reads the permanent user ID and then revokes the token: the portal never keeps one.
/// </summary>
public sealed class PortalIdentityExchange(HttpClient http, PortalProviderApps? apps = null, PortalProviderEndpoints? endpoints = null)
    : IPortalIdentityExchange
{
    public const string RedirectUri = PortalDirectory.PortalOrigin + "/auth/return";

    private readonly PortalProviderApps _apps = apps ?? PortalProviderApps.BuiltIn;
    private readonly PortalProviderEndpoints _endpoints = endpoints ?? PortalProviderEndpoints.Default;

    public JsonArray Describe()
    {
        var list = new JsonArray();
        foreach (var provider in Enum.GetValues<IdentityProvider>())
        {
            var app = _apps.For(provider);
            if (!app.IsSet) continue;
            var (authorize, scope, pkce, extra) = provider switch
            {
                IdentityProvider.Google => (_endpoints.GoogleAuthorize, "openid email profile", true, "&prompt=select_account"),
                IdentityProvider.Twitch => (_endpoints.TwitchAuthorize, "", false, "&force_verify=true"),
                _ => (_endpoints.DiscordAuthorize, "identify", true, "&prompt=consent"),
            };
            list.Add(new JsonObject
            {
                ["id"] = Id(provider),
                ["name"] = provider.ToString(),
                // The device adds state (and code_challenge where pkce is true).
                ["authorize"] = $"{authorize}?response_type=code&client_id={Uri.EscapeDataString(app.ClientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(scope)}{extra}",
                ["pkce"] = pkce,
            });
        }
        return list;
    }

    public static string Id(IdentityProvider provider) => provider.ToString().ToLowerInvariant();

    public static IdentityProvider? Parse(string? id) =>
        Enum.TryParse<IdentityProvider>(id, ignoreCase: true, out var p) && Enum.IsDefined(p) && !int.TryParse(id, out _) ? p : null;

    public async Task<ProviderIdentity> ExchangeAsync(IdentityProvider provider, string code, string verifier, CancellationToken ct)
    {
        var app = _apps.For(provider);
        if (!app.IsSet) throw new PortalAccessException($"This Cuebot cannot sign in with {provider}.");
        if (code.Length is 0 or > 2048 || verifier.Length > 256) throw new PortalAccessException("The sign-in code is not valid.");
        return provider switch
        {
            IdentityProvider.Google => await GoogleAsync(app, code, verifier, ct).ConfigureAwait(false),
            IdentityProvider.Twitch => await TwitchAsync(app, code, ct).ConfigureAwait(false),
            _ => await DiscordAsync(app, code, verifier, ct).ConfigureAwait(false),
        };
    }

    private async Task<ProviderIdentity> GoogleAsync(AppCredentials app, string code, string verifier, CancellationToken ct)
    {
        var token = await TokenAsync(_endpoints.GoogleToken, IdentityProvider.Google, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);
        try
        {
            var me = await GetAsync(_endpoints.GoogleUserInfo, IdentityProvider.Google, new AuthenticationHeaderValue("Bearer", token), null, ct).ConfigureAwait(false);
            var sub = Str(me, "sub");
            if (sub.Length == 0) throw Refused(IdentityProvider.Google);
            // An unverified address must not satisfy an invite locked to an email.
            var email = me["email_verified"]?.GetValueKind() == JsonValueKind.True ? Str(me, "email") : "";
            return new ProviderIdentity(IdentityProvider.Google, sub, email, Str(me, "name"));
        }
        finally
        {
            await RevokeAsync(_endpoints.GoogleRevoke, new() { ["token"] = token }).ConfigureAwait(false);
        }
    }

    private async Task<ProviderIdentity> TwitchAsync(AppCredentials app, string code, CancellationToken ct)
    {
        var token = await TokenAsync(_endpoints.TwitchToken, IdentityProvider.Twitch, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = app.ClientId,
            ["client_secret"] = app.ClientSecret,
            ["redirect_uri"] = RedirectUri,
        }, ct).ConfigureAwait(false);
        try
        {
            var valid = await GetAsync(_endpoints.TwitchValidate, IdentityProvider.Twitch, new AuthenticationHeaderValue("OAuth", token), null, ct).ConfigureAwait(false);
            // The token must be for this app: a code from another app's sign-in is not proof of anything here.
            if (Str(valid, "client_id") != app.ClientId || Str(valid, "user_id").Length == 0) throw Refused(IdentityProvider.Twitch);
            var id = Str(valid, "user_id");
            var login = Str(valid, "login");
            var display = login;
            try
            {
                var users = await GetAsync($"{_endpoints.TwitchUsers}?id={Uri.EscapeDataString(id)}", IdentityProvider.Twitch,
                    new AuthenticationHeaderValue("Bearer", token), app.ClientId, ct).ConfigureAwait(false);
                if (users["data"] is JsonArray { Count: > 0 } data && data[0] is JsonObject u && Str(u, "display_name") is { Length: > 0 } d) display = d;
            }
            catch (PortalAccessException)
            {
                // The login name is enough.
            }
            return new ProviderIdentity(IdentityProvider.Twitch, id, login, display);
        }
        finally
        {
            await RevokeAsync(_endpoints.TwitchRevoke, new() { ["client_id"] = app.ClientId, ["token"] = token }).ConfigureAwait(false);
        }
    }

    private async Task<ProviderIdentity> DiscordAsync(AppCredentials app, string code, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        };
        if (app.ClientSecret.Length > 0) form["client_secret"] = app.ClientSecret;
        var token = await TokenAsync(_endpoints.DiscordToken, IdentityProvider.Discord, form, ct).ConfigureAwait(false);
        try
        {
            var me = await GetAsync(_endpoints.DiscordMe, IdentityProvider.Discord, new AuthenticationHeaderValue("Bearer", token), null, ct).ConfigureAwait(false);
            var id = Str(me, "id");
            if (id.Length == 0) throw Refused(IdentityProvider.Discord);
            var name = Str(me, "username");
            return new ProviderIdentity(IdentityProvider.Discord, id, name, Str(me, "global_name") is { Length: > 0 } g ? g : name);
        }
        finally
        {
            var revoke = new Dictionary<string, string> { ["token"] = token, ["client_id"] = app.ClientId };
            if (app.ClientSecret.Length > 0) revoke["client_secret"] = app.ClientSecret;
            await RevokeAsync(_endpoints.DiscordRevoke, revoke).ConfigureAwait(false);
        }
    }

    private async Task<string> TokenAsync(string url, IdentityProvider provider, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) }, provider, ct)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, provider, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PortalAccessException($"{provider} did not accept the sign-in. Start the sign-in again.");
        return Str(body, "access_token") is { Length: > 0 } token ? token : throw Refused(provider);
    }

    private async Task<JsonObject> GetAsync(string url, IdentityProvider provider, AuthenticationHeaderValue auth, string? clientId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = auth;
        if (clientId is not null) request.Headers.Add("Client-Id", clientId);
        using var response = await SendAsync(request, provider, ct).ConfigureAwait(false);
        var body = await ReadAsync(response, provider, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? body : throw Refused(provider);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, IdentityProvider provider, CancellationToken ct)
    {
        using (request)
        {
            try { return await http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { throw new PortalAccessException($"Cuebot could not reach {provider}. Try again in a moment."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new PortalAccessException($"{provider} did not answer in time. Try again."); }
        }
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, IdentityProvider provider, CancellationToken ct)
    {
        try { return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? []; }
        catch (JsonException) { return response.IsSuccessStatusCode ? throw Refused(provider) : []; }
    }

    /// <summary>Best effort: the token was only needed to read the user ID.</summary>
    private async Task RevokeAsync(string url, Dictionary<string, string> form)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsync(url, new FormUrlEncodedContent(form), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // It expires by itself.
        }
    }

    private static PortalAccessException Refused(IdentityProvider provider) =>
        new($"{provider} did not say who signed in. Start the sign-in again.");

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : o[name] is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n.ToString() : "";
}
