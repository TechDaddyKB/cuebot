using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.YouTube.Api;
using Cuebot.YouTube.Auth;

namespace Cuebot.YouTube.Tests.Support;

/// <summary>A YouTube API client wired to a fake HTTP handler, with signed-in accounts in a temporary youtube.json.</summary>
internal sealed class TestApi : IDisposable
{
    public static readonly YouTubeEndpoints Endpoints =
        new(new Uri("https://oauth.test/"), new Uri("https://accounts.test/o/oauth2/v2/auth"), new Uri("https://api.test/youtube/v3/"));

    public static YouTubeTimings Fast { get; } = new(
        MinimumChatPoll: TimeSpan.FromMilliseconds(10),
        MinimumBroadcastPoll: TimeSpan.FromMilliseconds(20),
        MinimumLivePoll: TimeSpan.FromMilliseconds(20),
        MinimumSubscriberPoll: TimeSpan.FromMilliseconds(20),
        ErrorBackoff: TimeSpan.FromMilliseconds(10),
        MaximumErrorBackoff: TimeSpan.FromMilliseconds(50),
        TokenRefreshMargin: TimeSpan.FromSeconds(60));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-youtube-tests-" + Guid.NewGuid().ToString("N"));

    public TestApi(Func<RecordedRequest, HttpResponseMessage> respond, TimeProvider? time = null, YouTubeTimings? timings = null)
    {
        Directory.CreateDirectory(_dir);
        Time = time ?? new FakeTimeProvider();
        Timings = timings ?? Fast;
        Log = new AppLog();
        Settings = new JsonFile<YouTubeSettings>(Path.Combine(_dir, "youtube.json"), Log);
        Settings.Value.ClientId = "cid";
        Settings.Value.ClientSecret = "secret";
        SignIn(Settings.Value.Broadcaster, "BROADCASTER-TOKEN", ChatJson.Owner, "Owner");
        Http = new FakeHttpHandler(respond);
        Quota = new QuotaTracker(Time);
        var oauth = new GoogleOAuthClient(new HttpClient(Http), Endpoints, Time);
        Broadcaster = new AccountTokens(Settings, YouTubeAccountKind.Broadcaster, oauth, Time, Timings, Log);
        Bot = new AccountTokens(Settings, YouTubeAccountKind.Bot, oauth, Time, Timings, Log);
        Api = new YouTubeApi(new YouTubeApiClient(new HttpClient(Http), Endpoints.Api, Quota));
    }

    public TimeProvider Time { get; }
    public YouTubeTimings Timings { get; }
    public AppLog Log { get; }
    public JsonFile<YouTubeSettings> Settings { get; }
    public FakeHttpHandler Http { get; }
    public QuotaTracker Quota { get; }
    public AccountTokens Broadcaster { get; }
    public AccountTokens Bot { get; }
    public YouTubeApi Api { get; }

    public void SignIn(YouTubeAccount account, string token, string channelId, string title)
    {
        account.AccessToken = token;
        account.RefreshToken = "RT-" + token;
        account.AccessTokenExpiresAt = Time.GetUtcNow().AddHours(1);
        account.ChannelId = channelId;
        account.ChannelTitle = title;
    }

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
