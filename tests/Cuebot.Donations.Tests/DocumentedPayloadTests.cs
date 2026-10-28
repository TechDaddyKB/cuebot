using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.TreatStream;

namespace Cuebot.Donations.Tests;

/// <summary>
/// The example payloads of each service's official documentation, copied verbatim (fetched 2026-10-28), run through
/// the mappers. When a service changes its documented examples, these are the fixtures to update.
/// </summary>
public sealed class DocumentedPayloadTests
{
    private static System.Text.Json.JsonElement Parse(string json) => StreamlabsMappingTests.Parse(json);

    // ---- Streamlabs: https://dev.streamlabs.com/docs/socket-api (updated 2025-06-05) ----

    private const string SlDonation = """
        {"type":"donation","message":[{"id":96164121,"name":"test","amount":"13.37","formatted_amount":"$13.37","formattedAmount":"$13.37",
        "message":"test donation","currency":"USD","emotes":null,"iconClassName":"user","to":{"name":"Sai Harsha Maddela"},"from":"test",
        "from_user_id":null,"_id":"0820c9d5bafd768c9843f5e35c885e71"}],"event_id":"evt_17e5f4dc6888767ed9799f78dfa2cabc"}
        """;

    private const string SlTwitchSubscription = """
        {"type":"subscription","message":[{"name":"kevin","months":7,"message":"sub message","emotes":null,"sub_plan":"1000",
        "sub_plan_name":"Sample Plan name","sub_type":"resub","_id":"a226a8c223634afdc368439c1da7aaba"}],"for":"twitch_account"}
        """;

    private const string SlTwitchFollow = """
        {"type":"follow","message":[{"created_at":"2017-08-22 00:14:35","id":"170884026","name":"h4r5h48002","_id":"74a0b93e736f1f14762111f8ae34bf42"}],"for":"twitch_account"}
        """;

    private const string SlTwitchHost = """
        {"type":"host","message":[{"name":"h4r5h48002","viewers":"1","type":"manual","_id":"74a0b93e736f1f14762111f8ae34bf42"}],"for":"twitch_account"}
        """;

    private const string SlTwitchBits = """
        {"type":"bits","message":[{"id":"fc546f7d-aab8-42b2-9933-8681e9fb5eb0","name":"h4r5h48002","amount":"1","emotes":null,"message":"streamlabs1",
        "_id":"74a0b93e736f1f14762111f8ae34bf42"}],"for":"twitch_account"}
        """;

    /// <summary>The docs print this one as a JavaScript object literal (unquoted keys); quoted here, values unchanged.</summary>
    private const string SlTwitchRaid = """
        {"type":"raid","message":[{"name":"sunnyding602","raiders":138,"_id":"6f306e1fadf37130b5d5445b95b22436"}],"for":"twitch_account",
        "event_id":"evt_a6737f6334591943e5a04b0d792000d6"}
        """;

    private const string SlYouTubeSubscription = """
        {"type":"follow","message":[{"publishedAt":"2017-08-22 00:25:57","id":"UCec4hVEu3ZXE8qlRtUeK0DA","name":"Kappa Lord","_id":"6af0964e548a6d93c192c9e31e0959cc"}],"for":"youtube_account"}
        """;

    private const string SlYouTubeSponsor = """
        {"type":"subscription","message":[{"sponsorSince":"2017-08-22 00:45:48","id":"UCec4hVEu3ZXE8qlRtUeK0DA","name":"Kappa Lord",
        "channelUrl":"http://www.youtube.com/channel/UCec4hVEu3ZXE8qlRtUeK0DA","months":3,"_id":"33d396edab6e2e2e8b37a7b6f1b60b82"}],"for":"youtube_account"}
        """;

    private const string SlYouTubeSuperchat = """
        {"type":"superchat","message":[{"id":"LCC.Cg8KDQoLWmpUemdzdU1vM2sSHAoaQ05PcW01UzJ3dElDRll2WkhBb2RLd0lBOWc0.028141892513880107",
        "channelId":"UCec4hVEu3ZXE8qlRtUeK0DA","channelUrl":"http://www.youtube.com/channel/UCec4hVEu3ZXE8qlRtUeK0DA","name":"Kappa Lord",
        "comment":"love the stream","amount":"2000000","currency":"USD","displayString":"$2.00","messageType":2,"createdAt":"2017-08-22 00:51:57",
        "_id":"ad46c7e468331c5e39373e05876fa17e"}],"for":"youtube_account"}
        """;

    private const string SlMixerSubscription = """
        {"type":"subscription","message":[{"id":"123456","name":"h4r5h4","months":2,"message":null,"emotes":null,"since":"2017-08-22 00:36:59",
        "_id":"68f034214d68dce22431801b940d2b90"}],"for":"mixer_account"}
        """;

    private const string SlMixerFollow = """
        {"type":"follow","message":[{"id":1816,"name":"Jamacanbacn","created_at":"2017-08-22 00:30:25","_id":"4fbcdd82bc9fd39467572b24ee20a766"}],"for":"mixer_account"}
        """;

    private const string SlMixerHost = """
        {"type":"host","message":[{"name":"h4r5h47","viewers":"5","type":"manual","_id":"4ee5fcf7e34128f4f07bb978c9e35fd4"}],"for":"mixer_account"}
        """;

    [Fact]
    public void Streamlabs_documented_donation()
    {
        var e = StreamlabsMappingTests.One(SlDonation);
        var a = e.Args;
        Assert.Equal(StreamlabsEventTypes.Donation, e.Type);
        Assert.Equal(("test", 13.37, "USD", "$13.37", "test donation"),
            (a["donationFrom"], a["donationAmount"], a["donationCurrency"], a["donationFormattedAmount"], a["donationMessage"]));
        Assert.Equal(("96164121", "", "Sai Harsha Maddela", "user"), (a["donationId"], a["userId"], a["recipient"], a["iconClassName"]));
        Assert.Equal(["evt:evt_17e5f4dc6888767ed9799f78dfa2cabc:0", "donation:_id:0820c9d5bafd768c9843f5e35c885e71"], e.Keys);
    }

    [Fact]
    public void Streamlabs_donation_with_the_for_value_of_the_type_table_is_still_streamlabs_own()
    {
        // The docs' table lists donation with For = "streamlabs"; the sample has no "for" at all.
        var e = StreamlabsMappingTests.One(SlDonation.Replace("\"event_id\"", "\"for\":\"streamlabs\",\"event_id\"", StringComparison.Ordinal));
        Assert.Equal((StreamlabsEventTypes.Donation, false, "Streamlabs"), (e.Type, e.Relayed, e.Args["platform"]));
    }

    [Theory]
    [InlineData(SlTwitchSubscription, StreamlabsEventTypes.Subscription, "Twitch", "kevin")]
    [InlineData(SlTwitchFollow, StreamlabsEventTypes.Follow, "Twitch", "h4r5h48002")]
    [InlineData(SlTwitchHost, StreamlabsEventTypes.Host, "Twitch", "h4r5h48002")]
    [InlineData(SlTwitchBits, StreamlabsEventTypes.Bits, "Twitch", "h4r5h48002")]
    [InlineData(SlTwitchRaid, StreamlabsEventTypes.Raid, "Twitch", "sunnyding602")]
    [InlineData(SlYouTubeSubscription, StreamlabsEventTypes.Follow, "YouTube", "Kappa Lord")]
    [InlineData(SlYouTubeSponsor, StreamlabsEventTypes.Subscription, "YouTube", "Kappa Lord")]
    [InlineData(SlYouTubeSuperchat, StreamlabsEventTypes.Superchat, "YouTube", "Kappa Lord")]
    [InlineData(SlMixerSubscription, StreamlabsEventTypes.Subscription, "mixer", "h4r5h4")]
    [InlineData(SlMixerFollow, StreamlabsEventTypes.Follow, "mixer", "Jamacanbacn")]
    [InlineData(SlMixerHost, StreamlabsEventTypes.Host, "mixer", "h4r5h47")]
    public void Streamlabs_documented_platform_events_are_relayed(string json, string type, string platform, string user)
    {
        var e = StreamlabsMappingTests.One(json);
        Assert.Equal((type, true, platform, user), (e.Type, e.Relayed, e.Args["platform"], e.Args["user"]));
    }

    [Fact]
    public void Streamlabs_documented_values_keep_their_meaning()
    {
        var bits = StreamlabsMappingTests.One(SlTwitchBits).Args;
        Assert.Equal((1, "streamlabs1"), (bits["bits"], bits["message"]));   // "amount":"1" is a string
        Assert.Equal(1, StreamlabsMappingTests.One(SlTwitchHost).Args["viewers"]);   // "viewers":"1" too
        Assert.Equal(138, StreamlabsMappingTests.One(SlTwitchRaid).Args["viewers"]);
        Assert.Equal("2017-08-22 00:14:35", StreamlabsMappingTests.One(SlTwitchFollow).Args["createdAt"]);

        var sponsor = StreamlabsMappingTests.One(SlYouTubeSponsor).Args;
        Assert.Equal((3, "2017-08-22 00:45:48", "UCec4hVEu3ZXE8qlRtUeK0DA"), (sponsor["months"], sponsor["since"], sponsor["userId"]));
        Assert.Equal("", StreamlabsMappingTests.One(SlMixerSubscription).Args["message"]);   // "message":null

        // Superchat amounts are micros; "id" is the chat message, the sender is "channelId".
        var superchat = StreamlabsMappingTests.One(SlYouTubeSuperchat).Args;
        Assert.Equal((2.0, "USD", "$2.00", "love the stream"), (superchat["amount"], superchat["currency"], superchat["formattedAmount"], superchat["message"]));
        Assert.Equal("UCec4hVEu3ZXE8qlRtUeK0DA", superchat["userId"]);
        Assert.StartsWith("LCC.", (string)superchat["superchatId"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Streamlabs_raids_from_the_type_table_map_like_raid()
    {
        // The docs' table names the type "raids" while the sample sends "raid"; both are accepted.
        var e = StreamlabsMappingTests.One(SlTwitchRaid.Replace("\"type\":\"raid\"", "\"type\":\"raids\"", StringComparison.Ordinal));
        Assert.Equal((StreamlabsEventTypes.Raid, 138), (e.Type, e.Args["viewers"]));
    }

    // ---- StreamElements: https://docs.streamelements.com/websockets/topics/channel-activities ----

    /// <summary>The Astro message example of the Activities topic page, verbatim (its data is what the mapper gets).</summary>
    private const string SeActivityMessage = """
        {"id":"01HPPM2TQ6CCMFQGHEN8MM1NN0","ts":"2024-02-15T14:52:42Z","type":"message","topic":"channel.activities",
         "data":{"type":"follow","provider":"twitch","channel":"6183c7d4ebc632abbf809890",
          "data":{"username":"jtv","displayName":"jtv","providerId":"14027",
           "avatar":"https://static-cdn.jtvnw.net/user-default-pictures-uv/13e5fa74-defa-11e9-809c-784f43822e80-profile_image-300x300.png"},
          "_id":"65ce2539b1585c5f909a91e1","createdAt":"2024-02-15T14:52:41.608Z","updatedAt":"2024-02-15T14:52:41.608Z",
          "activityId":"65ce2539b1585c5f909a91e1","sessionEventsCount":6}}
        """;

    [Fact]
    public void StreamElements_documented_activity()
    {
        var e = StreamElementsEventMapper.Map(Parse(SeActivityMessage).GetProperty("data"))!;
        Assert.Equal((StreamElementsEventTypes.Follow, true), (e.Type, e.Relayed));
        Assert.Equal(("jtv", "14027", "Twitch", "6183c7d4ebc632abbf809890", "2024-02-15T14:52:41.608Z"),
            (e.Args["user"], e.Args["userId"], e.Args["platform"], e.Args["channelId"], e.Args["createdAt"]));
        Assert.StartsWith("https://static-cdn.jtvnw.net/", (string)e.Args["avatar"]!, StringComparison.Ordinal);
        Assert.Equal(["activity:65ce2539b1585c5f909a91e1"], e.Keys);
    }

    [Fact]
    public void StreamElements_documented_provider_spelling_StreamElements_is_streamelements_own()
    {
        Assert.Equal("StreamElements", StreamElementsEventMapper.Platform("StreamElements"));
        Assert.Equal("Fourthwall", StreamElementsEventMapper.Platform("fourthwall"));
    }

    // ---- TipeeeStream: https://api.tipeeestream.com/api-doc/socketio and /api-doc/events ----

    private const string TipeeeNewEvent = """
        {"appKey":"####","event":{"id":359821,"type":"donation",
         "user":{"avatar":"5236","country":"FR","username":"lilelulo",
          "providers":[{"connectedAt":"2015-10-05T15:28:12+0200","code":"twitch","id":"36820986","username":"lilelulo"}],
          "created_at":"2015-09-04T18:31:47+0200","session_at":"2015-09-04T18:31:47+0200"},
         "ref":"561290126af3f","inserted_at":"2015-10-05T16:58:26+0200","deleted_at":"2015-10-05T17:03:26+0200","created_at":"2015-10-05T16:58:26+0200",
         "parameters":{"formattedMessage":"take my moneeeeey!!","message":"take my moneeeeey!!","username":"anonym1","currency":"EUR","amount":1,"resub":0,"viewers":4},
         "formattedAmount":"€1.00","parameters.amount":1}}
        """;

    /// <summary>One entry of the documented <c>GET /v1.0/events.json</c> response, in a <c>new-event</c> wrapper.</summary>
    private const string TipeeeListedSubscription = """
        {"event":{"id":357246,"type":"subscription","user":{"avatar":"5236","country":"FR","username":"lilelulo",
         "providers":[{"connectedAt":"2015-10-07T16:25:44+0200","code":"twitch","id":"36820986","username":"lilelulo"}],
         "created_at":"2015-09-04T18:31:47+0200","session_at":"2015-09-04T18:31:47+0200"},
         "ref":"TWITCH_subscription_55f436032cd02","inserted_at":"2015-09-12T16:26:11+0200","created_at":"2015-09-12T16:26:11+0200",
         "parameters":{"resub":"0","twitch_channel_id":"#lilelulo","username":"greg"},"parameters.amount":0}}
        """;

    [Fact]
    public void TipeeeStream_documented_events()
    {
        var donation = TipeeeStreamEventMapper.Map(Parse(TipeeeNewEvent))!;
        Assert.Equal(("anonym1", 1d, "EUR", "€1.00", "take my moneeeeey!!", "359821"),
            (donation.Args["username"], donation.Args["amount"], donation.Args["currency"], donation.Args["formattedAmount"], donation.Args["message"],
             donation.Args["tipeeStreamId"]));
        Assert.Equal("2015-10-05T16:58:26.0000000+02:00", donation.Args["timestamp"]);

        var sub = TipeeeStreamEventMapper.Map(Parse(TipeeeListedSubscription))!;
        Assert.Equal((TipeeeStreamEventTypes.Subscription, true, "greg", 0, "Twitch"),
            (sub.Type, sub.Relayed, sub.Args["user"], sub.Args["months"], sub.Args["platform"]));
    }

    // ---- DonorDrive: https://github.com/DonorDrive/PublicAPI (resources/donations.md, resources/teams.md) ----

    private const string DdDonations = """
        [{"displayName":"Friendly Donor","participantID":4024,"amount":10,"donorID":"EB8610A3FC435D58",
          "avatarImageURL":"https://static.donordrive.com/clients/testdrive/img/avatar-constituent-default.gif","createdDateUTC":"2019-10-30T18:01:18.513+0000",
          "eventID":581,"teamID":5074,"donationID":"DF4E676D0828A8D5",
          "links":{"recipient":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.participant&participantID=4024",
                   "donate":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.donate&participantID=4024"},
          "isRegFee":false,"recipientName":"Jane Participant",
          "recipientImageURL":"https://testdrive.donordrivecontent.com/try/images/$avatars$/constituent_8672DB7B-CE87-F677-6260FF8F15074828.jpg",
          "message":null,"activityPledgeUnitAmount":10.00,"activityPledgeMaxAmount":1000.00},
         {"displayName":"Jane Participant","participantID":4024,"amount":10,"donorID":"DC3A81BE19445E15","isRegFee":false,
          "avatarImageURL":"https://static.donordrive.com/clients/testdrive/img/avatar-constituent-default.gif","createdDateUTC":"2019-10-28T16:02:42.583+0000",
          "eventID":581,"teamID":5074,"donationID":"F491FB1141299348",
          "links":{"recipient":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.participant&participantID=4024",
                   "donate":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.donate&participantID=4024"},
          "incentiveID":"213F6CC8-9CFE-C59F-C3FE158798AFA7B1","recipientName":"Jane Participant",
          "recipientImageURL":"https://testdrive.donordrivecontent.com/try/images/$avatars$/constituent_8672DB7B-CE87-F677-6260FF8F15074828.jpg",
          "message":null,"donorIsRecipient":true},
         {"displayName":"The Donation Foundation","participantID":4024,"amount":100,"isRegFee":false,"donorID":"03F279F2BF01283D",
          "avatarImageURL":"https://static.donordrive.com/clients/testdrive/img/avatar-constituent-default.gif","createdDateUTC":"2019-10-26T15:26:13.290+0000",
          "eventID":581,"teamID":5074,"donationID":"3FF734E8C1657F6C",
          "links":{"recipient":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.participant&participantID=4024",
                   "donate":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.donate&participantID=4024"},
          "recipientName":"Jane Participant",
          "recipientImageURL":"https://testdrive.donordrivecontent.com/try/images/$avatars$/constituent_8672DB7B-CE87-F677-6260FF8F15074828.jpg",
          "message":null}]
        """;

    private const string DdTeam = """
        [{"numParticipants":5,"fundraisingGoal":20000,"eventName":"Test Participant Event",
          "links":{"donate":"https://testdrive.donordrive.com/index.cfm?fuseaction=donate.team&teamID=8775",
                   "page":"https://testdrive.donordrive.com/index.cfm?fuseaction=donorDrive.team&teamID=8775"},
          "createdDateUTC":"2019-11-02T15:02:38.93+0000","eventID":508,"sumDonations":9349.5,"name":"The Bonhams","isInviteOnly":false,
          "captainDisplayName":"Liam Bonham","avatarImageURL":"https://static.donordrive.com/clients/testdrive/img/avatar-team-default.gif",
          "teamID":8775,"sumPledges":0,"numDonations":97}]
        """;

    [Fact]
    public void DonorDrive_documented_team_and_donations()
    {
        var team = DonorDriveEventMapper.Profile(Parse(DdTeam), isTeam: true)!;
        Assert.Equal(("8775", "The Bonhams", "", 20000d, 9349.5, 97), (team.Id, team.Name, team.TeamName, team.Goal, team.Raised, team.NumDonations));

        var list = Parse(DdDonations).EnumerateArray().ToArray();
        var recent = list.Select(DonorDriveEventMapper.Summary).ToArray();
        var e = DonorDriveEventMapper.Donation(list[1], team, null, "USD", recent: recent);
        var a = e.Args;
        Assert.Equal(("Jane Participant", 10d, "", true, false), (a["donorName"], a["donorAmount"], a["donorMessage"], a["donorIsRecipient"], a["donorIsAnonymous"]));
        Assert.Equal(("Jane Participant", true, false, 97), (a["recipientName"], a["isTeam"], a["isParticipant"], a["numDonations"]));
        var donations = Assert.IsType<List<Dictionary<string, object?>>>(a["donations"]);
        Assert.Equal(["Friendly Donor", "Jane Participant", "The Donation Foundation"], donations.Select(d => (string)d["donorName"]!));
        Assert.Equal((100d, "2019-10-26T15:26:13.2900000+00:00"), (donations[2]["donorAmount"], donations[2]["createdAt"]));
    }

    // ---- TreatStream: https://treatstream.com/api/details ("Real time treat data example") ----

    [Fact]
    public void TreatStream_documented_treat()
    {
        var e = TreatStreamEventMapper.Map(Parse(TreatStreamMappingTests.Treat))!;
        Assert.Equal(("treatstream", "Pizza", "enjoy treat", "2024-03-16T14:21:44.0000000+00:00"),
            (e.Args["username"], e.Args["type"], e.Args["message"], e.Args["timestamp"]));
        // The REST list (getMonthTreats) uses letter codes for the same fields.
        Assert.Equal(("Facebook", "Twitch"), (TreatStreamEventMapper.Platform("f"), TreatStreamEventMapper.Platform("t")));
    }
}
