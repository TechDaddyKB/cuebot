using System.Net;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class StreamElementsApiTests
{
    private const string Jwt = "jwt-SECRET-value-0123456789";
    private const string Channel = "5ad23dcc18fff500d78c5348";
    private readonly FakeHttpHandler _http;
    private readonly StreamElementsApi _api;

    public StreamElementsApiTests()
    {
        _http = new FakeHttpHandler(Respond);
        _api = new StreamElementsApi(new HttpClient(_http), new Uri("https://api.test/kappa/v2/"));
    }

    private static HttpResponseMessage Respond(RecordedRequest r) => (r.Method.Method, r.Uri.PathAndQuery) switch
    {
        ("GET", "/kappa/v2/channels/me") => FakeHttpHandler.Json($$"""{"_id":"{{Channel}}","username":"techdaddy","displayName":"TechDaddy"}"""),
        ("GET", $"/kappa/v2/points/{Channel}/alice") =>
            FakeHttpHandler.Json($$"""{"channel":"{{Channel}}","username":"alice","points":1500,"pointsAlltime":4200,"rank":3}"""),
        ("PUT", $"/kappa/v2/points/{Channel}/alice/-50") =>
            FakeHttpHandler.Json($$"""{"channel":"{{Channel}}","username":"alice","amount":-50,"newAmount":1450,"message":"Removed 50 points"}"""),
        ("GET", $"/kappa/v2/points/{Channel}/top?limit=2&offset=0") =>
            FakeHttpHandler.Json("""{"_total":40,"users":[{"username":"alice","points":1500},{"username":"bob","points":900}]}"""),
        ("GET", $"/kappa/v2/tips/{Channel}?limit=2&offset=0&sort=-createdAt") => FakeHttpHandler.Json($$"""
            {"docs":[{"donation":{"user":{"username":"Styler","geo":"ZZ","email":"styler@example.com"},"message":"gg","amount":4.2,"currency":"USD",
            "paymentMethod":"scheme"},"_id":"t1","channel":"{{Channel}}","provider":"paypal","approved":"allowed","status":"success",
            "createdAt":"2025-02-19T15:07:09.302Z"},{"donation":{"user":{"username":"Gone"},"amount":1,"currency":"USD"},"deleted":true,"_id":"t0"}],"total":2,"limit":2,"offset":0}
            """),
        // The documented example of GET tips/{channel}/top (dev.streamelements.com), plus two more rows.
        ("GET", $"/kappa/v2/tips/{Channel}/top") => FakeHttpHandler.Json("""
            [{"username":"StreamElements","first":"2018-12-07T17:19:01.957Z","last":"2018-12-07T17:19:01.957Z","count":1,"total":100,"email":"streamelements@streamelements.com","average":100},
             {"username":"Styler","first":"2025-02-19T15:07:09.302Z","last":"2025-02-19T15:07:09.302Z","count":3,"total":104.2,"email":"","average":34.73},
             {"username":"bob","first":"2025-01-01T00:00:00.000Z","last":"2025-01-01T00:00:00.000Z","count":1,"total":20,"email":"","average":20}]
            """),
        ("GET", $"/kappa/v2/overlays/{Channel}") => FakeHttpHandler.Json("""{"docs":[{"_id":"o1","name":"Main","settings":{"width":1920,"height":1080}}]}"""),
        ("GET", $"/kappa/v2/overlays/{Channel}/o1") => FakeHttpHandler.Json("""{"_id":"o1","name":"Main","widgets":[{"type":"text"}]}"""),
        _ => FakeHttpHandler.Json("""{"statusCode":401,"error":"Unauthorized","message":"Invalid jwt-SECRET-value-0123456789"}""", HttpStatusCode.Unauthorized),
    };

    [Fact]
    public async Task Overlays_are_listed_and_read_with_get_requests_only()
    {
        var list = await _api.GetOverlaysAsync(Jwt, Channel, default);
        Assert.Equal("Main", list!["docs"]![0]!["name"]!.GetValue<string>());
        var overlay = await _api.GetOverlayAsync(Jwt, Channel, "o1", default);
        Assert.Equal("text", overlay["widgets"]![0]!["type"]!.GetValue<string>());
        Assert.All(_http.Requests, r => Assert.Equal(("GET", "Bearer " + Jwt), (r.Method.Method, r.Authorization)));
        var error = await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.GetOverlayAsync(Jwt, Channel, "missing", default));
        Assert.DoesNotContain(Jwt, error.Message);
    }

    [Fact]
    public async Task Requests_use_the_kappa_v2_paths_and_a_bearer_jwt()
    {
        var channel = await _api.GetMyChannelAsync(Jwt, default);
        Assert.Equal(new StreamElementsChannel(Channel, "techdaddy", "TechDaddy"), channel);

        Assert.Equal(new StreamElementsPoints("alice", 1500, 4200, 3), await _api.GetUserPointsAsync(Jwt, Channel, "alice", default));
        Assert.Equal(1450, await _api.AddUserPointsAsync(Jwt, Channel, "alice", -50, default));
        Assert.Equal([new StreamElementsRank("alice", 1500), new StreamElementsRank("bob", 900)],
            await _api.GetPointsLeaderboardAsync(Jwt, Channel, 2, default));

        var tip = Assert.Single(await _api.GetTipsAsync(Jwt, Channel, 2, default));
        Assert.Equal(("t1", "Styler", 4.2, "USD", "gg", "$4.20"), (tip.Id, tip.UserName, tip.Amount, tip.Currency, tip.Message, tip.FormattedAmount));

        Assert.Equal([new StreamElementsRank("Styler", 104.2), new StreamElementsRank("StreamElements", 100)],
            await _api.GetTipLeaderboardAsync(Jwt, Channel, 2, default));

        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + Jwt, r.Authorization));
        Assert.Equal(HttpMethod.Put, _http.Requests[2].Method);
    }

    [Fact]
    public async Task Errors_never_contain_the_token()
    {
        var ex = await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.GetUserPointsAsync(Jwt, Channel, "nobody", default));
        Assert.Equal(401, ex.Status);
        Assert.Contains("rejected", ex.Message);
        Assert.DoesNotContain(Jwt, ex.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.GetMyChannelAsync(" ", default));
        Assert.Single(_http.Requests);   // a blank token never reaches the network
    }

    [Fact]
    public async Task User_names_are_escaped_and_limits_clamped()
    {
        await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.GetUserPointsAsync(Jwt, Channel, "a b/c", default));
        await Assert.ThrowsAsync<StreamElementsApiException>(() => _api.GetTipsAsync(Jwt, Channel, 1000, default));
        Assert.Equal($"/kappa/v2/points/{Channel}/a%20b%2Fc", _http.Requests[0].Uri.AbsolutePath);
        Assert.Contains("limit=100", _http.Requests[1].Uri.Query);
    }
}
