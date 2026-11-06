using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Auth;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.SignIn;

namespace Cuebot.Portal.Tests;

public sealed class PortalSignInHandlerTests
{
    private sealed class FakeExchange : IPortalIdentityExchange
    {
        public ProviderIdentity Identity { get; set; } = new(IdentityProvider.Google, "1001", "mod@example.com", "Mod One");
        public int Calls { get; private set; }
        public (IdentityProvider Provider, string Code, string Verifier) Last { get; private set; }

        public JsonArray Describe() => [new JsonObject { ["id"] = "google" }];

        public Task<ProviderIdentity> ExchangeAsync(IdentityProvider provider, string code, string verifier, CancellationToken ct)
        {
            Calls++;
            Last = (provider, code, verifier);
            if (code == "bad") throw new PortalAccessException("Google did not accept the sign-in.");
            return Task.FromResult(Identity with { Provider = provider });
        }
    }

    private readonly PortalDirectory _dir = new(new PortalSettings(), () => { });
    private readonly FakeExchange _exchange = new();
    private readonly List<(Guid Member, string Op)> _apiCalls = [];
    private readonly PortalSignInHandler _handler;
    private readonly ECDsa _device = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public PortalSignInHandlerTests() => _handler = new PortalSignInHandler(_dir, _exchange, (m, r) =>
    {
        _apiCalls.Add((m, (string)r["op"]!));
        return Task.FromResult(new JsonObject { ["ok"] = true, ["name"] = _dir.Member(m)!.Name, ["role"] = _dir.RoleOf(_dir.Member(m)!)!.Name });
    });

    private static PortalLinkContext NewLink() => new(7, RandomNumberGenerator.GetBytes(32));

    private JsonObject Device(PortalLinkContext link, ECDsa? key = null, byte[]? binding = null)
    {
        var k = key ?? _device;
        return new JsonObject
        {
            ["key"] = PortalKeys.Base64Url(PortalKeys.RawPublicKey(k.ExportParameters(false))),
            ["name"] = "Chrome on Windows",
            ["proof"] = PortalKeys.Base64Url(k.SignData(PortalHandshake.DeviceProof(binding ?? link.Binding), HashAlgorithmName.SHA256)),
        };
    }

    private async Task<JsonObject> Send(PortalLinkContext link, JsonObject request)
    {
        var answer = await _handler.HandleAsync(link, JsonSerializer.SerializeToUtf8Bytes(request), CancellationToken.None);
        return (JsonObject)JsonNode.Parse(answer!)!;
    }

    private Task<JsonObject> SignIn(PortalLinkContext link, string? invite, string code = "code-1", JsonObject? device = null)
    {
        var request = new JsonObject { ["op"] = "signin", ["req"] = 3, ["provider"] = "google", ["code"] = code, ["verifier"] = "verifier-1", ["device"] = device ?? Device(link) };
        if (invite is not null) request["invite"] = invite;
        return Send(link, request);
    }

    [Fact]
    public async Task An_invite_signs_the_mod_in_with_the_invites_role()
    {
        var invite = _dir.CreateInvite(DefaultRoles.LeadModId);
        var link = NewLink();
        var answer = await SignIn(link, invite.Secret);

        Assert.True((bool)answer["ok"]!);
        Assert.Equal("welcome", (string?)answer["op"]);
        Assert.Equal(3, (int)answer["req"]!);
        Assert.Equal("Lead Mod", (string?)answer["role"]);
        Assert.Equal((IdentityProvider.Google, "code-1", "verifier-1"), _exchange.Last);
        var member = Assert.Single(_dir.Members);
        Assert.Equal(DefaultRoles.LeadModId, member.RoleId);
        Assert.Equal("Chrome on Windows", Assert.Single(member.Devices).Name);
        Assert.Equal(member.Id, link.MemberId);
        Assert.Empty(_dir.Invites);
    }

    [Fact]
    public async Task Requests_need_a_sign_in_first_then_go_to_the_api_as_the_member()
    {
        var link = NewLink();
        var before = await Send(link, new JsonObject { ["op"] = "actions.list", ["req"] = 1 });
        Assert.False((bool)before["ok"]!);
        Assert.True((bool)before["signIn"]!);
        Assert.Empty(_apiCalls);

        await SignIn(link, _dir.CreateInvite(DefaultRoles.StandardModId).Secret);
        var after = await Send(link, new JsonObject { ["op"] = "actions.list", ["req"] = 2 });
        Assert.True((bool)after["ok"]!);
        Assert.Equal(2, (int)after["req"]!);
        Assert.Contains((link.MemberId!.Value, "actions.list"), _apiCalls);
    }

    [Fact]
    public async Task A_proof_from_another_connection_is_refused_before_the_provider_is_asked()
    {
        var invite = _dir.CreateInvite(DefaultRoles.StandardModId);
        var link = NewLink();
        var answer = await SignIn(link, invite.Secret, device: Device(link, binding: RandomNumberGenerator.GetBytes(32)));

        Assert.False((bool)answer["ok"]!);
        Assert.Contains("prove", (string?)answer["error"]);
        Assert.Equal(0, _exchange.Calls);
        Assert.Single(_dir.Invites);
        Assert.Null(link.MemberId);
    }

    [Fact]
    public async Task A_provider_refusal_reaches_the_mod_and_keeps_the_invite()
    {
        var invite = _dir.CreateInvite(DefaultRoles.StandardModId);
        var answer = await SignIn(NewLink(), invite.Secret, code: "bad");
        Assert.Equal("Google did not accept the sign-in.", (string?)answer["error"]);
        Assert.Single(_dir.Invites);
    }

    [Fact]
    public async Task A_member_who_opens_another_invite_signs_in_without_using_it_up()
    {
        await SignIn(NewLink(), _dir.CreateInvite(DefaultRoles.StandardModId).Secret);
        var second = _dir.CreateInvite(DefaultRoles.TechLeadId);
        var answer = await SignIn(NewLink(), second.Secret);
        Assert.True((bool)answer["ok"]!);
        Assert.Equal("Standard Mod", (string?)answer["role"]);
        Assert.Single(_dir.Invites);
    }

    [Fact]
    public async Task A_known_device_resumes_without_the_provider()
    {
        await SignIn(NewLink(), _dir.CreateInvite(DefaultRoles.StandardModId).Secret);
        var link = NewLink();
        var answer = await Send(link, new JsonObject { ["op"] = "resume", ["device"] = Device(link) });
        Assert.True((bool)answer["ok"]!);
        Assert.Equal("welcome", (string?)answer["op"]);
        Assert.Equal(1, _exchange.Calls);
        Assert.NotNull(link.MemberId);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var other = NewLink();
        var refused = await Send(other, new JsonObject { ["op"] = "resume", ["device"] = Device(other, stranger) });
        Assert.True((bool)refused["signIn"]!);
        Assert.Null(other.MemberId);
    }

    [Fact]
    public async Task Without_an_invite_only_members_get_in()
    {
        var answer = await SignIn(NewLink(), invite: null);
        Assert.False((bool)answer["ok"]!);
        Assert.Contains("not a member", (string?)answer["error"]);
        Assert.Empty(_dir.Members);
    }

    [Fact]
    public async Task A_suspended_member_is_signed_out_of_the_connection()
    {
        var link = NewLink();
        await SignIn(link, _dir.CreateInvite(DefaultRoles.StandardModId).Secret);
        _dir.SetSuspended(link.MemberId!.Value, true);
        var answer = await Send(link, new JsonObject { ["op"] = "me" });
        Assert.True((bool)answer["signIn"]!);
        Assert.Null(link.MemberId);
    }

    [Fact]
    public async Task Signing_out_forgets_the_device()
    {
        var link = NewLink();
        await SignIn(link, _dir.CreateInvite(DefaultRoles.StandardModId).Secret);
        Assert.True((bool)(await Send(link, new JsonObject { ["op"] = "signout" }))["ok"]!);
        Assert.Empty(Assert.Single(_dir.Members).Devices);
        Assert.Null(link.MemberId);
    }

    [Fact]
    public async Task Sign_in_attempts_per_connection_are_limited()
    {
        var link = NewLink();
        for (var i = 0; i < PortalSignInHandler.MaxSignInAttempts; i++) await SignIn(link, "no-such-invite", code: "bad");
        var answer = await SignIn(link, "no-such-invite");
        Assert.Contains("Too many", (string?)answer["error"]);
        Assert.Equal(PortalSignInHandler.MaxSignInAttempts, _exchange.Calls);
    }

    [Fact]
    public async Task Ping_providers_and_garbage()
    {
        var link = NewLink();
        Assert.Equal("pong", (string?)(await Send(link, new JsonObject { ["op"] = "ping" }))["op"]);
        var providers = await Send(link, new JsonObject { ["op"] = "providers" });
        Assert.Equal("https://portal.techdaddydigital.com/auth/return", (string?)providers["redirectUri"]);
        Assert.Single(providers["providers"]!.AsArray());
        var garbage = (JsonObject)JsonNode.Parse((await _handler.HandleAsync(link, "not json"u8.ToArray(), CancellationToken.None))!)!;
        Assert.False((bool)garbage["ok"]!);
    }
}

public sealed class PortalIdentityExchangeTests
{
    private sealed class FakeProvider : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Auth, string ClientId, Dictionary<string, string> Form)> Requests { get; } = [];
        public Func<string, (HttpStatusCode, string)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = new Dictionary<string, string>();
            if (request.Content is not null)
                foreach (var pair in (await request.Content.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    form[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
                }
            var url = request.RequestUri!.ToString();
            Requests.Add((request.Method, url, request.Headers.Authorization?.ToString() ?? "",
                request.Headers.TryGetValues("Client-Id", out var c) ? c.Single() : "", form));
            var (status, body) = Answer(url);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly PortalProviderEndpoints Endpoints = new(
        "https://accounts.google.com/auth", "https://g.test/token", "https://g.test/userinfo", "https://g.test/revoke",
        "https://id.twitch.tv/authorize", "https://t.test/token", "https://t.test/validate", "https://t.test/users", "https://t.test/revoke",
        "https://discord.com/authorize", "https://d.test/token", "https://d.test/me", "https://d.test/revoke");

    private static readonly PortalProviderApps Apps = new(new("gid", "gsecret", true), new("tid", "tsecret", true), new("did", "", true));

    private readonly FakeProvider _fake = new();
    private readonly PortalIdentityExchange _exchange;

    public PortalIdentityExchangeTests() => _exchange = new PortalIdentityExchange(new HttpClient(_fake), Apps, Endpoints);

    [Fact]
    public async Task Google_exchanges_with_pkce_reads_the_verified_email_and_revokes_the_token()
    {
        _fake.Answer = url => url switch
        {
            "https://g.test/token" => (HttpStatusCode.OK, """{"access_token":"at-1"}"""),
            "https://g.test/userinfo" => (HttpStatusCode.OK, """{"sub":"1001","email":"mod@example.com","email_verified":true,"name":"Mod One"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        };
        var who = await _exchange.ExchangeAsync(IdentityProvider.Google, "code-1", "ver-1", CancellationToken.None);

        Assert.Equal(new ProviderIdentity(IdentityProvider.Google, "1001", "mod@example.com", "Mod One"), who);
        var token = _fake.Requests[0];
        Assert.Equal("code-1", token.Form["code"]);
        Assert.Equal("ver-1", token.Form["code_verifier"]);
        Assert.Equal("gsecret", token.Form["client_secret"]);
        Assert.Equal(PortalIdentityExchange.RedirectUri, token.Form["redirect_uri"]);
        Assert.Equal("Bearer at-1", _fake.Requests[1].Auth);
        Assert.Equal(("https://g.test/revoke", "at-1"), (_fake.Requests[2].Url, _fake.Requests[2].Form["token"]));
    }

    [Fact]
    public async Task An_unverified_Google_email_is_not_used()
    {
        _fake.Answer = url => url == "https://g.test/token"
            ? (HttpStatusCode.OK, """{"access_token":"at-1"}""")
            : (HttpStatusCode.OK, """{"sub":"1001","email":"someone@example.com","email_verified":false}""");
        Assert.Equal("", (await _exchange.ExchangeAsync(IdentityProvider.Google, "c", "v", CancellationToken.None)).Login);
    }

    [Fact]
    public async Task Twitch_checks_the_token_is_for_this_app()
    {
        _fake.Answer = url => url switch
        {
            "https://t.test/token" => (HttpStatusCode.OK, """{"access_token":"tt-1"}"""),
            "https://t.test/validate" => (HttpStatusCode.OK, """{"client_id":"tid","login":"modone","user_id":"555"}"""),
            "https://t.test/users?id=555" => (HttpStatusCode.OK, """{"data":[{"display_name":"ModOne"}]}"""),
            _ => (HttpStatusCode.OK, "{}"),
        };
        var who = await _exchange.ExchangeAsync(IdentityProvider.Twitch, "code-t", "", CancellationToken.None);
        Assert.Equal(new ProviderIdentity(IdentityProvider.Twitch, "555", "modone", "ModOne"), who);
        Assert.False(_fake.Requests[0].Form.ContainsKey("code_verifier"));
        Assert.Equal("OAuth tt-1", _fake.Requests[1].Auth);
        Assert.Equal("tid", _fake.Requests[2].ClientId);
        Assert.Equal("tid", _fake.Requests[3].Form["client_id"]);

        _fake.Requests.Clear();
        _fake.Answer = url => url == "https://t.test/token"
            ? (HttpStatusCode.OK, """{"access_token":"tt-2"}""")
            : (HttpStatusCode.OK, """{"client_id":"someone-else","login":"x","user_id":"1"}""");
        await Assert.ThrowsAsync<PortalAccessException>(() => _exchange.ExchangeAsync(IdentityProvider.Twitch, "c", "", CancellationToken.None));
        Assert.Contains(_fake.Requests, r => r.Url == "https://t.test/revoke");
    }

    [Fact]
    public async Task Discord_signs_in_without_a_secret()
    {
        _fake.Answer = url => url switch
        {
            "https://d.test/token" => (HttpStatusCode.OK, """{"access_token":"dt-1"}"""),
            "https://d.test/me" => (HttpStatusCode.OK, """{"id":"8080","username":"modone","global_name":"Mod One"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        };
        var who = await _exchange.ExchangeAsync(IdentityProvider.Discord, "code-d", "ver-d", CancellationToken.None);
        Assert.Equal(new ProviderIdentity(IdentityProvider.Discord, "8080", "modone", "Mod One"), who);
        Assert.False(_fake.Requests[0].Form.ContainsKey("client_secret"));
        Assert.Equal("ver-d", _fake.Requests[0].Form["code_verifier"]);
    }

    [Fact]
    public async Task A_refused_code_says_so_in_plain_words()
    {
        _fake.Answer = _ => (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var e = await Assert.ThrowsAsync<PortalAccessException>(() => _exchange.ExchangeAsync(IdentityProvider.Google, "c", "v", CancellationToken.None));
        Assert.Contains("did not accept", e.Message);
    }

    [Fact]
    public void Describes_only_the_providers_with_an_app()
    {
        var some = new PortalIdentityExchange(new HttpClient(_fake), Apps with { Twitch = AppCredentials.None }, Endpoints).Describe();
        Assert.Equal(["google", "discord"], some.Select(p => (string)p!["id"]!));
        var google = new Uri((string)some[0]!["authorize"]!);
        Assert.Contains("client_id=gid", google.Query);
        Assert.Contains("scope=openid%20email%20profile", google.Query);
        Assert.Contains("redirect_uri=https%3A%2F%2Fportal.techdaddydigital.com%2Fauth%2Freturn", google.Query);
        Assert.True((bool)some[0]!["pkce"]!);
    }

    [Fact]
    public void Parses_provider_ids()
    {
        Assert.Equal(IdentityProvider.Twitch, PortalIdentityExchange.Parse("twitch"));
        Assert.Null(PortalIdentityExchange.Parse("1"));
        Assert.Null(PortalIdentityExchange.Parse("github"));
        Assert.Null(PortalIdentityExchange.Parse(null));
    }
}
