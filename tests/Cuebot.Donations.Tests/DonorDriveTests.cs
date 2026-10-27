using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.Tests.Support;

namespace Cuebot.Donations.Tests;

public sealed class DonorDriveMappingTests
{
    /// <summary>The participant example of the DonorDrive Public API docs (resources/participants.md).</summary>
    internal const string Participant = """
        {"displayName":"Liam Bonham","fundraisingGoal":8000,"eventName":"Test Participant Event",
         "links":{"donate":"https://testdrive.donordrive.com/index.cfm?fuseaction=donordrive.participant&participantID=19265#donate",
                  "page":"https://testdrive.donordrive.com/index.cfm?fuseaction=donordrive.participant&participantID=19265"},
         "createdDateUTC":"2019-11-02T15:02:38.93+0000","eventID":508,"sumDonations":4661,"participantID":19265,"teamName":"The Bonhams",
         "avatarImageURL":"https://static.donordrive.com/clients/testdrive/img/avatar-constituent-default.gif","teamID":8775,
         "isTeamCaptain":true,"sumPledges":0,"numDonations":51}
        """;

    /// <summary>The donation example of the docs (resources/donations.md), with an incentive.</summary>
    internal const string Donation = """
        {"displayName":"Jane Participant","participantID":4024,"amount":10,"donorID":"DC3A81BE19445E15","isRegFee":false,
         "avatarImageURL":"https://static.donordrive.com/clients/try/img/avatar-constituent-default.gif",
         "createdDateUTC":"2019-10-28T16:02:42.583+0000","eventID":581,"teamID":5074,"donationID":"F491FB1141299348",
         "links":{"recipient":"https://try.donordrive.com/participant/4024","donate":"https://try.donordrive.com/participant/4024#donate"},
         "incentiveID":"213F6CC8-9CFE-C59F-C3FE158798AFA7B1","recipientName":"Jane Participant",
         "recipientImageURL":"https://static.donordrive.com/clients/try/img/avatar-constituent-default.gif","message":null,"donorIsRecipient":true}
        """;

    /// <summary>The incentive example of the docs (resources/incentives.md).</summary>
    internal const string Incentive = """
        {"endDateUTC":"2021-04-14T07:00:00.0+0000","amount":22,"description":"Spin the prize wheel!","quantity":5,"quantityClaimed":0,
         "incentiveID":"213F6CC8-9CFE-C59F-C3FE158798AFA7B1","isActive":false,"startDateUTC":"2021-04-01T07:00:00.0+0000"}
        """;

    [Theory]
    [InlineData("2019-10-28T16:02:42.583+0000", "2019-10-28T16:02:42.5830000+00:00")]
    [InlineData("2019-11-02T15:02:38.93+0000", "2019-11-02T15:02:38.9300000+00:00")]
    [InlineData("2021-04-14T07:00:00.0+0000", "2021-04-14T07:00:00.0000000+00:00")]
    [InlineData("garbage", "garbage")]
    [InlineData("", "")]
    public void Parses_donordrive_dates(string raw, string iso) => Assert.Equal(iso, DonorDriveEventMapper.Date(raw));

    [Theory]
    [InlineData("www.charity.org", "https://www.charity.org/api/")]
    [InlineData("https://www.charity.org/", "https://www.charity.org/api/")]
    [InlineData("https://www.charity.org/api", "https://www.charity.org/api/")]
    [InlineData("https://www.charity.org/api/?x=1", "https://www.charity.org/api/")]
    public void Normalizes_custom_sites(string site, string api) => Assert.Equal(api, DonorDriveApi.Normalize(site).AbsoluteUri);

    [Fact]
    public void Program_presets_and_bad_custom_sites()
    {
        Assert.Equal("https://dd.extra-life.org/api/", DonorDriveApi.BaseFor(new DonorDriveSettings()).AbsoluteUri);
        Assert.Equal("testdrive.donordrive.com", DonorDriveApi.BaseFor(new DonorDriveSettings { Program = DonorDriveProgram.TestDrive }).Host);
        Assert.Throws<DonorDriveApiException>(() => DonorDriveApi.BaseFor(new DonorDriveSettings { Program = DonorDriveProgram.Custom }));
        Assert.Throws<DonorDriveApiException>(() => DonorDriveApi.Normalize("ftp://x.org"));
        Assert.Equal("teams/8775", DonorDriveApi.ProfilePath(DonorDriveProfileType.Team, " 8775 "));
    }

    [Fact]
    public void Maps_participants_donations_and_incentives_with_streamerbot_names()
    {
        var profile = DonorDriveEventMapper.Profile(StreamlabsMappingTests.Parse(Participant), isTeam: false)!;
        Assert.Equal(("19265", "Liam Bonham", "The Bonhams", 8000d, 4661d, 51), (profile.Id, profile.Name, profile.TeamName, profile.Goal, profile.Raised, profile.NumDonations));
        var incentive = DonorDriveEventMapper.Incentive(StreamlabsMappingTests.Parse(Incentive))!;
        Assert.Equal(("Spin the prize wheel!", 22d, 5, false), (incentive.Description, incentive.Amount, incentive.Quantity, incentive.IsActive));

        var e = DonorDriveEventMapper.Donation(StreamlabsMappingTests.Parse(Donation), profile, incentive, "USD");
        var a = e.Args;
        Assert.Equal(DonorDriveEventTypes.Donation, e.Type);
        Assert.Equal(["donordrive:donation:F491FB1141299348"], e.Keys);
        Assert.Equal(("Jane Participant", 10d, "$10.00", "", false), (a["donorName"], a["donorAmount"], a["formattedAmount"], a["donorMessage"], a["donorIsAnonymous"]));
        Assert.Equal(("Liam Bonham", "The Bonhams", 8000d, 4661d, 51, true, false), (a["profileName"], a["profileTeamName"], a["goal"], a["raised"], a["donations"], a["isParticipant"], a["isTeam"]));
        Assert.Equal((true, "Spin the prize wheel!", 22d, "2021-04-01T07:00:00.0000000+00:00"), (a["isIncentive"], a["incentive.description"], a["incentive.amount"], a["incentive.startDate"]));
        Assert.Equal("2019-10-28T16:02:42.5830000+00:00", a["createdAt"]);

        var incentiveEvent = DonorDriveEventMapper.IncentiveEvent(e, "F491FB1141299348");
        Assert.Equal((DonorDriveEventTypes.Incentive, "Spin the prize wheel!"), (incentiveEvent.Type, incentiveEvent.Args["incentive.description"]));
    }

    [Fact]
    public void Anonymous_donors_and_unlimited_incentives()
    {
        var profile = DonorDriveEventMapper.Profile(StreamlabsMappingTests.Parse(Participant), false)!;
        var a = DonorDriveEventMapper.Donation(StreamlabsMappingTests.Parse("""{"donationID":"A1","createdDateUTC":"2026-01-01T00:00:00.0+0000"}"""), profile, null, "USD").Args;
        Assert.Equal(("Anonymous", true, 0d, "", false), (a["user"], a["donorIsAnonymous"], a["amount"], a["formattedAmount"], a["isIncentive"]));
        Assert.Equal(-1, DonorDriveEventMapper.Incentive(StreamlabsMappingTests.Parse("""{"incentiveID":"i","amount":5}"""))!.Quantity);
    }
}

public sealed class DonorDriveServiceTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-donordrive-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly object _gate = new();
    private string _participant = DonorDriveMappingTests.Participant;
    private string _profileTag = "\"p1\"";
    private string _donations = "[]";
    private Func<RecordedRequest, HttpResponseMessage?>? _override;
    private JsonFile<DonationsSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private DonorDriveService _service = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.DonorDrive.ProfileId = "19265";
        _settings.Value.DonorDrive.Program = DonorDriveProgram.Custom;
        _settings.Value.DonorDrive.CustomBaseUrl = "https://charity.test";
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _http = new FakeHttpHandler(Respond);
        _service = new DonorDriveService(_settings, bus, _log, new DonorDriveApi(new HttpClient(_http)), DonorDriveService.MinimumInterval)
        {
            IntervalOverride = TimeSpan.FromMilliseconds(50),
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private HttpResponseMessage Respond(RecordedRequest r)
    {
        if (_override?.Invoke(r) is { } overridden) return overridden;
        lock (_gate)
        {
            switch (r.Uri.AbsolutePath)
            {
                case "/api/participants/19265":
                    if (r.Headers.TryGetValue("If-None-Match", out var tag) && tag == _profileTag)
                        return new HttpResponseMessage(HttpStatusCode.NotModified);
                    var profile = FakeHttpHandler.Json(_participant);
                    profile.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(_profileTag);
                    return profile;
                case "/api/participants/19265/donations":
                    return FakeHttpHandler.Json(_donations);
                case "/api/participants/19265/incentives":
                    return FakeHttpHandler.Json("[" + DonorDriveMappingTests.Incentive + "]");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>Not Found</html>", Encoding.UTF8, "text/html") };
            }
        }
    }

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private static string Gift(string id, double amount, string? incentive = null) =>
        $$"""{"displayName":"Donor {{id}}","amount":{{amount}},"donationID":"{{id}}","message":"hi {{id}}","createdDateUTC":"2026-01-01T00:00:00.0+0000"{{(incentive is null ? "" : $",\"incentiveID\":\"{incentive}\"")}}}""";

    private void Change(string donations, double raised, int count)
    {
        lock (_gate)
        {
            _donations = donations;
            _participant = DonorDriveMappingTests.Participant.Replace("\"sumDonations\":4661", $"\"sumDonations\":{raised}").Replace("\"numDonations\":51", $"\"numDonations\":{count}");
            _profileTag = $"\"p{count}\"";
        }
    }

    [Fact]
    public async Task First_poll_learns_the_state_then_new_donations_incentives_and_profile_changes_are_published()
    {
        _donations = "[" + Gift("OLD1", 5) + "]";
        await _service.PollAsync(first: true, default);
        Assert.Empty(_published);
        Assert.Equal("Liam Bonham", _service.Profile!.Name);

        // Nothing changed: only the profile is asked for, with If-None-Match, and answered 304.
        var before = _http.Requests.Count;
        await _service.PollAsync(first: false, default);
        var conditional = Assert.Single(_http.Requests.Skip(before));
        Assert.Equal("\"p1\"", conditional.Headers["If-None-Match"]);
        Assert.Empty(_published);

        Change("[" + Gift("NEW2", 22, "213F6CC8-9CFE-C59F-C3FE158798AFA7B1") + "," + Gift("NEW1", 10) + "," + Gift("OLD1", 5) + "]", 4693, 53);
        await _service.PollAsync(first: false, default);
        var donations = Events(DonorDriveEventTypes.Donation);
        Assert.Equal(["NEW1", "NEW2"], donations.Select(d => (string)d.Args["donationId"]!));   // oldest first
        Assert.Equal(("Donor NEW1", "$10.00", 4693d, 53), (donations[0].Args["user"], donations[0].Args["formattedAmount"], donations[0].Args["raised"], donations[0].Args["donations"]));
        var incentive = Assert.Single(Events(DonorDriveEventTypes.Incentive));
        Assert.Equal(("Donor NEW2", "Spin the prize wheel!"), (incentive.Args["user"], incentive.Args["incentive.description"]));
        var updated = Assert.Single(Events(DonorDriveEventTypes.ProfileUpdated));
        Assert.Equal((4693d, 4661d), (updated.Args["raised"], updated.Args["previousRaised"]));
        Assert.Contains(_service.RecentEvents(), r => r.Summary == "Donor NEW2 claimed the incentive \"Spin the prize wheel!\"");
    }

    [Fact]
    public async Task Keeps_checking_the_donation_list_when_it_lags_behind_the_profile()
    {
        await _service.PollAsync(first: true, default);
        Change("[]", 4671, 52);   // the profile already counts it, the list is still cached
        await _service.PollAsync(first: false, default);
        Assert.Empty(Events(DonorDriveEventTypes.Donation));
        lock (_gate) _donations = "[" + Gift("LATE", 10) + "]";
        await _service.PollAsync(first: false, default);   // profile 304, list checked again
        Assert.Single(Events(DonorDriveEventTypes.Donation));
    }

    [Fact]
    public async Task Connect_polls_in_the_background_and_reports_connected()
    {
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected");
        Assert.Contains("Liam Bonham", _service.StatusMessage);
        Assert.Single(Events(DonorDriveEventTypes.Connected));
        Change("[" + Gift("LIVE", 15) + "]", 4676, 52);
        await Wait.UntilAsync(() => Events(DonorDriveEventTypes.Donation).Length == 1, "donation");
        Assert.All(_http.Requests, r => Assert.Equal("charity.test", r.Uri.Host));
    }

    [Fact]
    public async Task An_unknown_id_stops_with_an_error()
    {
        _settings.Value.DonorDrive.ProfileId = "404404";
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Error, "error");
        Assert.Contains("no such participant", _service.StatusMessage);
    }

    [Fact]
    public async Task Rate_limits_are_reported_and_retried()
    {
        var limited = 2;
        _override = r => Interlocked.Decrement(ref limited) >= 0
            ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("You've been rate limited, take a break.") }
            : null;
        var ex = await Assert.ThrowsAsync<DonorDriveApiException>(() => _service.PollAsync(true, default));
        Assert.Equal(429, ex.Status);
        await _service.ConnectAsync();
        await Wait.UntilAsync(() => _service.State == ConnectionState.Connected, "connected after the rate limit");
    }

    [Fact]
    public void Test_events_cover_donation_incentive_and_profile()
    {
        Assert.True(_service.SendTestEvent("donation"));
        Assert.True(_service.SendTestEvent("incentive"));
        Assert.True(_service.SendTestEvent("profile"));
        Assert.False(_service.SendTestEvent("other"));
        Assert.Equal(2, Events(DonorDriveEventTypes.Donation).Length);
        Assert.Single(Events(DonorDriveEventTypes.Incentive));
        Assert.All(_published, e => Assert.Equal(true, e.Args["isTest"]));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Missing_id_fails_connect()
    {
        _settings.Value.DonorDrive.ProfileId = "";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ConnectAsync());
        Assert.Contains("participant or team id", ex.Message);
    }
}
