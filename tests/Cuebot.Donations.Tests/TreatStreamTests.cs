using System.Collections.Concurrent;
using System.Net;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.Tests.Support;
using Cuebot.Donations.TreatStream;

namespace Cuebot.Donations.Tests;

public sealed class TreatStreamMappingTests
{
    /// <summary>The realTimeTreat sample of TreatStream's API page (treatstream.com/api/details).</summary>
    internal const string Treat = """{"message":"enjoy treat","sender":"treatstream","receiver":"kumokgb","title":"Pizza","sender_type":"Twitch","receiver_type":"Twitch","date_created":"2024-03-16 14:21:44 "}""";

    [Fact]
    public void Maps_the_documented_treat_with_streamerbot_names()
    {
        var e = TreatStreamEventMapper.Map(StreamlabsMappingTests.Parse(Treat))!;
        Assert.Equal(TreatStreamEventTypes.Treat, e.Type);
        var a = e.Args;
        Assert.Equal(("treatstream", "Pizza", "enjoy treat", "2024-03-16T14:21:44.0000000+00:00"), (a["username"], a["type"], a["message"], a["timestamp"]));
        Assert.Equal(("kumokgb", "Twitch", "Twitch"), (a["receiver"], a["senderType"], a["receiverType"]));
        Assert.Equal(["treat:treatstream|Pizza|2024-03-16 14:21:44|enjoy treat"], e.Keys);
    }

    [Fact]
    public void Rest_letter_codes_and_junk()
    {
        Assert.Equal("Twitch", TreatStreamEventMapper.Platform("t"));
        Assert.Equal("Facebook", TreatStreamEventMapper.Platform("f"));
        Assert.Null(TreatStreamEventMapper.Map(StreamlabsMappingTests.Parse("{}")));
        Assert.Equal("Anonymous", TreatStreamEventMapper.Map(StreamlabsMappingTests.Parse("""{"title":"Cake"}"""))!.Args["user"]);
    }

    [Fact]
    public void Treats_show_in_the_feed_with_the_treat_as_amount()
    {
        var e = TreatStreamEventMapper.Map(StreamlabsMappingTests.Parse(Treat))!;
        var item = new TreatStreamChatAdapter().ToFeedItem(new BotEvent(e.Type, e.Args))!;
        Assert.Equal(("TreatStream", Cuebot.Core.Chat.FeedKind.Tip, "treatstream sent a Pizza treat", "Pizza"), (item.Platform, item.Kind, item.Title, item.Amount));
    }

    [Fact]
    public void Parses_redirects()
    {
        Assert.Equal("abc", OAuthRedirects.Parse("?code=abc&state=s1", "s1", "TreatStream").Code);
        Assert.Contains("state mismatch", OAuthRedirects.Parse("?code=abc&state=zz", "s1", "TreatStream").Error);
        Assert.Contains("cancelled", OAuthRedirects.Parse("?error=access_denied&state=s1", "s1", "TreatStream").Error);
    }
}

public sealed class TreatStreamServiceTests : IAsyncLifetime
{
    private const string Access = "TREAT-SECRET-access-token-123456";
    private const string Secret = "TREAT-SECRET-client-secret-98765";
    private const string Socket = "TREAT-SECRET-socket-token-555555";
    private const string Open = """0{"sid":"s1","upgrades":["websocket"],"pingInterval":25000,"pingTimeout":60000}""";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-treat-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private readonly FakeReceiver _receiver = new();
    private HttpStatusCode _socketStatus = HttpStatusCode.OK;
    private JsonFile<DonationsSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private TreatStreamService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        var s = _settings.Value.TreatStream;
        s.ClientId = "client-1";
        s.ClientSecret = Secret;
        s.AccessToken = Access;
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _http = new FakeHttpHandler(Respond);
        _service = new TreatStreamService(_settings, bus, _log, _server.Create, new Uri("https://nodeapi.treat.test/"), TestTimings.Fast,
            new TreatStreamApi(new HttpClient(_http), new Uri("https://treat.test/")), _ => _receiver, TimeProvider.System);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r) => r.Uri.AbsolutePath switch
    {
        "/Oauth2/Authorize/socketToken" => _socketStatus == HttpStatusCode.OK
            ? FakeHttpHandler.Json($$"""{"socket_token":"{{Socket}}"}""")
            : FakeHttpHandler.Json("""{"error":"invalid_token"}""", _socketStatus),
        "/Oauth2/Authorize/token" => FakeHttpHandler.Json("""{"access_token":"NEW-ACCESS-TOKEN-abcdef","expires_in":864000,"token_type":"Bearer","scope":"userinfo","refresh_token":"NEW-REFRESH-TOKEN-abcdef"}"""),
        "/Oauth2/RefreshToken" => FakeHttpHandler.Json("""{"access_token":"REFRESHED-ACCESS-abcdef","expires_in":864000,"token_type":"Bearer"}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    [Fact]
    public async Task Gets_a_socket_token_connects_with_it_and_publishes_treats_once()
    {
        await _service.ConnectAsync();
        var request = Assert.Single(_http.Requests);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Contains("client_id=client-1", request.Body);
        Assert.Contains($"access_token={Access}", request.Body);
        Assert.Single(Events(TreatStreamEventTypes.Authenticated));

        var socket = await _server.NextAsync();
        Assert.Equal("nodeapi.treat.test", socket.Url!.Host);
        Assert.Contains($"token={Socket}", socket.Url.Query);
        Assert.Contains("EIO=3", socket.Url.Query);
        socket.Push(Open);
        socket.Push("40");
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");

        var frame = "42[\"realTimeTreat\"," + TreatStreamMappingTests.Treat + "]";
        socket.Push(frame);
        socket.Push(frame);
        await Wait.UntilAsync(() => Events(TreatStreamEventTypes.Treat).Length == 1, "treat");
        await Task.Delay(100);
        Assert.Single(Events(TreatStreamEventTypes.Treat));
    }

    [Fact]
    public async Task A_rejected_access_token_fails_connect()
    {
        _socketStatus = HttpStatusCode.Unauthorized;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Contains("Sign in again", ex.Message);
        Assert.Empty(Events(TreatStreamEventTypes.Authenticated));
    }

    [Fact]
    public async Task Refreshes_an_expiring_access_token_before_connecting()
    {
        _settings.Value.TreatStream.RefreshToken = "OLD-REFRESH-TOKEN-xyz";
        _settings.Value.TreatStream.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
        await _service.ConnectAsync();
        Assert.Equal("REFRESHED-ACCESS-abcdef", _settings.Value.TreatStream.AccessToken);
        Assert.Equal("OLD-REFRESH-TOKEN-xyz", _settings.Value.TreatStream.RefreshToken);   // kept when none is returned
        Assert.Contains(_http.Requests, r => r.Uri.AbsolutePath == "/Oauth2/Authorize/socketToken" && r.Body.Contains("REFRESHED-ACCESS-abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sign_in_opens_the_authorize_page_and_saves_the_tokens()
    {
        _settings.Value.TreatStream.AccessToken = "";
        string? opened = null;
        await _service.SignInAsync(url =>
        {
            opened = url;
            var state = Uri.UnescapeDataString(url.Split("state=")[1].Split('&')[0]);
            _receiver.Result = new OAuthRedirect("the-code", null);
            _receiver.ExpectedState = state;
        });
        Assert.StartsWith("https://treat.test/Oauth2/Authorize?response_type=code&client_id=client-1", opened);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A8913%2Fcallback", opened);
        var s = _settings.Value.TreatStream;
        Assert.Equal(("NEW-ACCESS-TOKEN-abcdef", "NEW-REFRESH-TOKEN-abcdef"), (s.AccessToken, s.RefreshToken));
        Assert.True(s.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddDays(9));
        var exchange = Assert.Single(_http.Requests);
        Assert.Contains("code=the-code", exchange.Body);
        Assert.Contains("grant_type=authorization_code", exchange.Body);
    }

    [Fact]
    public async Task Sign_in_needs_the_app_credentials_and_status_never_shows_secrets()
    {
        _settings.Value.TreatStream.ClientSecret = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignInAsync(_ => { }));
        _settings.Value.TreatStream.AccessToken = "";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Contains("Sign in to TreatStream", ex.Message);
        Assert.True(_service.SendTestEvent("treat"));
        Assert.Equal(true, Assert.Single(Events(TreatStreamEventTypes.Treat)).Args["isTest"]);
    }

    private sealed class FakeReceiver : IOAuthRedirectReceiver
    {
        public OAuthRedirect Result { get; set; } = new(null, "not set");
        public string ExpectedState { get; set; } = "";

        public Task<OAuthRedirect> WaitAsync(string expectedState, CancellationToken ct) =>
            Task.FromResult(expectedState == ExpectedState ? Result : new OAuthRedirect(null, "state mismatch"));

        public void Dispose()
        {
        }
    }
}
