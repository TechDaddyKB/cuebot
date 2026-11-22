using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Tests.ChatExtras;

/// <summary>YouTube member emotes from the streamer's own code → image list, plus the overlay's Shared Chat source.</summary>
public sealed class YouTubeMemberEmoteTests : IDisposable
{
    private const string Image = "https://example.test/dent.png";
    private readonly TempDir _dir = new();
    private readonly AppLog _log = new();
    private readonly EmoteSettings _settings = new() { SevenTv = false, Bttv = false, Ffz = false };
    private readonly EmoteService _emotes;
    private readonly AssetStore _assets = new(null);

    public YouTubeMemberEmoteTests() =>
        _emotes = new EmoteService(new EventBus(_log), _log, new EmoteDiskCache(_dir.Path, _log), () => _settings, []);

    public void Dispose()
    {
        _emotes.Dispose();
        _assets.Dispose();
        _dir.Dispose();
    }

    private void Add(string code, string url = Image) => _settings.YouTubeEmotes.Add(new CustomEmote { Code = code, ImageUrl = url });

    [Fact]
    public void A_listed_code_becomes_an_emote_on_youtube_only()
    {
        Add(":_tamaDent:");

        var match = Assert.Single(_emotes.FindEmotes("YouTube", "nice :_tamaDent: one"));
        Assert.Equal(5, match.Start);
        Assert.Equal(":_tamaDent:", match.Code);
        Assert.Equal(Image, match.ImageUrl);
        Assert.Equal(EmoteProviders.YouTube, match.Provider);
        Assert.Empty(_emotes.FindEmotes("Twitch", "nice :_tamaDent: one"));
    }

    [Fact]
    public void Codes_with_no_space_between_them_are_all_found()
    {
        Add(":_a:");
        Add(":_b:", "https://example.test/b.png");

        var matches = _emotes.FindEmotes("YouTube", "x:_a::_b::_a:y");

        Assert.Equal([":_a:", ":_b:", ":_a:"], matches.Select(m => m.Code));
        Assert.Equal([1, 5, 9], matches.Select(m => m.Start));
    }

    [Fact]
    public void A_colon_before_a_code_does_not_hide_it()
    {
        Add(":_a:");

        Assert.Equal(":_a:", Assert.Single(_emotes.FindEmotes("YouTube", "time 12:30 :_a:")).Code);
        Assert.Equal(":_a:", Assert.Single(_emotes.FindEmotes("YouTube", "no:pe:_a:")).Code);
    }

    [Fact]
    public void Codes_are_case_sensitive_and_unlisted_ones_stay_text()
    {
        Add(":_tamaDent:");

        Assert.Empty(_emotes.FindEmotes("YouTube", ":_TAMADENT: :_other:"));
    }

    [Theory]
    [InlineData("_name", ":_name:")]
    [InlineData(":_name:", ":_name:")]
    [InlineData("  :_name  ", ":_name:")]
    [InlineData("two words", "")]
    [InlineData("a:b", "")]
    [InlineData(":::", "")]
    public void Codes_are_normalized(string typed, string expected) => Assert.Equal(expected, YouTubeEmotes.NormalizeCode(typed));

    [Theory]
    [InlineData("https://example.test/a.png", true)]
    [InlineData("http://192.0.2.1/a.png", true)]
    [InlineData("/assets/abc.png", true)]
    [InlineData("/assets/../secret.png", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/svg+xml,<svg/>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void Only_web_addresses_and_stored_files_are_images(string url, bool expected) => Assert.Equal(expected, YouTubeEmotes.IsImageUrl(url));

    [Fact]
    public void A_row_with_a_bad_image_is_ignored()
    {
        Add(":_bad:", "javascript:alert(1)");

        Assert.Empty(_emotes.FindEmotes("YouTube", ":_bad:"));
    }

    [Fact]
    public void Listed_emotes_are_offered_in_autocomplete()
    {
        Add(":_tamaDent:");

        Assert.Equal(":_tamaDent:", Assert.Single(_emotes.Search("YouTube", ":_tama")).Code);
    }

    [Fact]
    public void A_change_to_the_list_tells_the_chat_page()
    {
        EmoteSetChange? change = null;
        _emotes.EmotesChanged += c => change = c;
        Add(":_a:");

        _emotes.NotifyCustomEmotesChanged();

        Assert.Equal(EmoteProviders.YouTube, change?.Provider);
        Assert.Equal(1, change?.Count);
    }

    [Fact]
    public async Task Importing_a_file_names_it_and_replaces_the_same_code()
    {
        var ct = CancellationToken.None;
        Assert.Equal(":_tamaDent:", await YouTubeEmotes.ImportAsync(_settings, _assets, "tamaDent.png", new MemoryStream([1, 2, 3]), ct));
        var first = Assert.Single(_settings.YouTubeEmotes).ImageUrl;
        Assert.StartsWith("/assets/", first);
        Assert.True(YouTubeEmotes.IsImageUrl(first));
        Assert.NotNull(_assets.PathOf(first["/assets/".Length..]));

        await YouTubeEmotes.ImportAsync(_settings, _assets, "tamaDent.png", new MemoryStream([4, 5, 6]), ct);

        Assert.NotEqual(first, Assert.Single(_settings.YouTubeEmotes).ImageUrl);
    }

    [Fact]
    public async Task Files_that_cannot_be_emotes_are_refused()
    {
        var ct = CancellationToken.None;
        await Assert.ThrowsAsync<ArgumentException>(() => YouTubeEmotes.ImportAsync(_settings, _assets, "clip.mp4", new MemoryStream([1]), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => YouTubeEmotes.ImportAsync(_settings, _assets, "icon.svg", new MemoryStream([1]), ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => YouTubeEmotes.ImportAsync(_settings, _assets, "empty.png", new MemoryStream(), ct));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            YouTubeEmotes.ImportAsync(_settings, _assets, "big.png", new MemoryStream(new byte[YouTubeEmotes.MaxImageBytes + 1]), ct));
        Assert.Empty(_settings.YouTubeEmotes);
    }

    [Fact]
    public void The_overlay_renders_a_listed_emote_as_an_image()
    {
        Add(":_tamaDent:");
        var matches = _emotes.FindEmotes("YouTube", "hi :_tamaDent:");

        var json = OverlayChatMessage.Build(Args("hi :_tamaDent:", "YouTube"), new Dictionary<string, object?>(), matches, [], "youtube", DateTimeOffset.UnixEpoch);

        Assert.Contains($"<img src=\"{Image}\"", json["renderedText"]!.GetValue<string>());
    }

    [Fact]
    public void The_overlay_payload_names_the_shared_chat_source()
    {
        var shared = new Dictionary<string, object?>
        {
            ["isSharedChat"] = true,
            ["sourceBroadcasterId"] = "2002",
            ["sourceBroadcaster"] = "Partner",
            ["sourceBroadcasterUserName"] = "partner",
            ["sourceBroadcasterProfileUrl"] = "https://example.test/p.png",
        };

        var source = OverlayChatMessage.Build(Args("hi", "Twitch"), shared, [], [], "twitch", DateTimeOffset.UnixEpoch)["data"]!["source"]!;

        Assert.Equal("2002", source["id"]!.GetValue<string>());
        Assert.Equal("Partner", source["name"]!.GetValue<string>());
        Assert.Equal("partner", source["login"]!.GetValue<string>());
        Assert.Equal("https://example.test/p.png", source["avatar"]!.GetValue<string>());

        var own = OverlayChatMessage.Build(Args("hi", "Twitch"), new Dictionary<string, object?> { ["isSharedChat"] = false }, [], [], "twitch", DateTimeOffset.UnixEpoch);
        Assert.Null(own["data"]!["source"]);
    }

    private static Dictionary<string, object?> Args(string message, string platform) => new()
    {
        ["platform"] = platform, ["user"] = "Viewer", ["userId"] = "9", ["messageId"] = "m1", ["message"] = message,
    };
}
