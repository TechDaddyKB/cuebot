namespace Cuebot.LiveTests;

/// <summary>Always-on unit tests for the live-test configuration helpers.</summary>
public class EnvFileTests
{
    [Fact]
    public void Parses_comments_quotes_and_export_prefix()
    {
        string[] lines =
        [
            "# stream keys",
            "",
            "CUEBOT_LIVE_STREAM=1",
            "export CUEBOT_LIVE_OBS_SCENE=\"Cuebot Dev Testing\"",
            "CUEBOT_LIVE_KICK_RTMP_URL='rtmps://ingest.example/app/'",
            "KEY_WITH_EQUALS=a=b",
            "not a pair",
        ];

        Assert.Equal(
            [
                ("CUEBOT_LIVE_STREAM", "1"),
                ("CUEBOT_LIVE_OBS_SCENE", "Cuebot Dev Testing"),
                ("CUEBOT_LIVE_KICK_RTMP_URL", "rtmps://ingest.example/app/"),
                ("KEY_WITH_EQUALS", "a=b"),
            ],
            EnvFile.Parse(lines));
    }

    [Fact]
    public void Stream_targets_never_print_their_key()
    {
        var target = new StreamTarget("kick", "rtmps://ingest.example/app/", "sk_secret_value");
        Assert.DoesNotContain("sk_secret_value", target.ToString());
    }

    [Fact]
    public void Known_platforms_have_default_ingest_urls_and_kick_does_not()
    {
        Assert.StartsWith("rtmp://", StreamTarget.DefaultUrl("twitch"));
        Assert.StartsWith("rtmps://", StreamTarget.DefaultUrl("YouTube"));
        Assert.Null(StreamTarget.DefaultUrl("kick"));
    }
}
