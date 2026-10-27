using System.Net;
using Cuebot.Core.Chat.Emotes;

namespace Cuebot.Tests.ChatExtras;

public class EmoteProviderTests
{
    private readonly FakeHttp _http = new();
    private static readonly EmoteChannel Twitch = new("Twitch", "123");

    public EmoteProviderTests() => ProviderJson.AllProviders(_http);

    [Fact]
    public async Task SevenTv_global_parses_names_images_and_zero_width_flags()
    {
        var emotes = await new SevenTvEmoteProvider(_http.Client()).FetchGlobalAsync(default);

        Assert.Equal(["peepoHappy", "RainTime"], emotes.Select(e => e.Code));
        var happy = emotes[0];
        Assert.Equal("7TV", happy.Provider);
        Assert.Equal("https://cdn.7tv.app/emote/60ae958e229664e8667aea38/1x.png", happy.ImageUrl);   // PNG preferred over WebP/AVIF
        Assert.False(happy.IsZeroWidth);
        Assert.Null(happy.Platform);
        var rain = emotes[1];
        Assert.True(rain.IsZeroWidth);
        Assert.True(rain.IsAnimated);
        Assert.EndsWith("/1x.gif", rain.ImageUrl);
    }

    [Fact]
    public async Task SevenTv_channel_reads_the_active_emote_set_and_falls_back_to_webp()
    {
        var emotes = await new SevenTvEmoteProvider(_http.Client()).FetchChannelAsync(Twitch, default);

        Assert.Equal(["catJAM", "KEKW"], emotes.Select(e => e.Code));
        Assert.Equal("https://cdn.7tv.app/emote/aaa/1x.webp", emotes[0].ImageUrl);
        Assert.Equal("https://cdn.7tv.app/emote/bbb/1x.webp", emotes[1].ImageUrl);
        Assert.All(emotes, e => Assert.Equal("Twitch", e.Platform));
        Assert.All(emotes, e => Assert.Equal("channel", e.Scope));
        Assert.Contains("https://7tv.io/v3/users/twitch/123", _http.Requests);
    }

    [Fact]
    public async Task Bttv_global_and_channel_include_shared_emotes_and_overlay_flags()
    {
        var bttv = new BttvEmoteProvider(_http.Client());

        var global = await bttv.FetchGlobalAsync(default);
        var channel = await bttv.FetchChannelAsync(Twitch, default);

        Assert.Equal(["OhMyGoodness", "SoSnowy"], global.Select(e => e.Code));
        Assert.Equal("https://cdn.betterttv.net/emote/54fa925e01e468494b85b54d/1x", global[0].ImageUrl);
        Assert.True(global[1].IsZeroWidth);
        Assert.True(global[1].IsAnimated);
        Assert.Equal(["ourEmote", "KEKW", "catJAM"], channel.Select(e => e.Code));
    }

    [Fact]
    public async Task Ffz_global_uses_only_default_sets_and_room_prefers_animated_urls()
    {
        var ffz = new FfzEmoteProvider(_http.Client());

        var global = await ffz.FetchGlobalAsync(default);
        var room = await ffz.FetchChannelAsync(Twitch, default);

        Assert.Equal(["CatBag"], global.Select(e => e.Code));
        Assert.Equal("https://cdn.frankerfacez.com/emote/25927/1", global[0].ImageUrl);
        Assert.Equal(["ffzChannel", "ffzHat"], room.Select(e => e.Code));
        Assert.Equal("https://cdn.frankerfacez.com/emote/5/animated/1", room[0].ImageUrl);
        Assert.True(room[0].IsAnimated);
        Assert.True(room[1].IsZeroWidth);
        Assert.False(ffz.SupportsPlatform("YouTube"));
    }

    [Fact]
    public async Task Unknown_channels_are_empty_and_server_errors_throw()
    {
        var unknown = new EmoteChannel("Twitch", "999");
        Assert.Empty(await new SevenTvEmoteProvider(_http.Client()).FetchChannelAsync(unknown, default));
        Assert.Empty(await new BttvEmoteProvider(_http.Client()).FetchChannelAsync(unknown, default));
        Assert.Empty(await new FfzEmoteProvider(_http.Client()).FetchChannelAsync(unknown, default));

        _http.Json("https://7tv.io/v3/emote-sets/global", "oops", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => new SevenTvEmoteProvider(_http.Client()).FetchGlobalAsync(default));
    }

    [Fact]
    public async Task Youtube_and_kick_channels_use_the_platform_key()
    {
        _http.Json("https://7tv.io/v3/users/kick/42", ProviderJson.SevenTvChannel);
        _http.Json("https://api.betterttv.net/3/cached/users/youtube/UCabc", ProviderJson.BttvChannel);

        var kick = await new SevenTvEmoteProvider(_http.Client()).FetchChannelAsync(new EmoteChannel("Kick", "42"), default);
        var youtube = await new BttvEmoteProvider(_http.Client()).FetchChannelAsync(new EmoteChannel("YouTube", "UCabc"), default);

        Assert.Equal(2, kick.Count);
        Assert.Equal(3, youtube.Count);
    }
}
