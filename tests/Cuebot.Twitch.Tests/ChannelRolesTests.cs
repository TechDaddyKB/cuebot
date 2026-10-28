using System.Net;
using Cuebot.Core.Logging;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>The moderator lookup Speech uses for whispered !tts commands (Helix moderation/moderators, cached).</summary>
public sealed class ChannelRolesTests : IDisposable
{
    private readonly ApiHarness _h = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Asks_helix_once_per_user_until_the_cache_expires()
    {
        _h.On("GET /helix/moderation/moderators", """{"data":[{"user_id":"4242","user_login":"moddy","user_name":"Moddy"}],"pagination":{}}""");
        var roles = new TwitchChannelRoles(() => _h.Api, new AppLog(), _time);

        Assert.Equal("Twitch", roles.Platform);
        Assert.True(await roles.IsModeratorAsync("4242"));
        Assert.True(await roles.IsModeratorAsync("4242"));
        Assert.Single(_h.Http.Requests, r => r.Path == "/helix/moderation/moderators");
        Assert.Contains("broadcaster_id=1337", _h.Last.Query);
        Assert.Contains("user_id=4242", _h.Last.Query);

        _h.On("GET /helix/moderation/moderators", """{"data":[],"pagination":{}}""");
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await roles.IsModeratorAsync("4242"));
        Assert.Equal(2, _h.Http.Requests.Count(r => r.Path == "/helix/moderation/moderators"));
    }

    [Fact]
    public async Task A_failed_lookup_is_not_a_moderator()
    {
        _h.On("GET /helix/moderation/moderators", """{"error":"Unauthorized","status":401,"message":"Missing scope"}""", HttpStatusCode.Unauthorized);
        var roles = new TwitchChannelRoles(() => _h.Api, new AppLog(), _time);

        Assert.False(await roles.IsModeratorAsync("4242"));
        Assert.False(await roles.IsModeratorAsync(""));
    }
}
