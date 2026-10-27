using System.Net;
using System.Text;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.ChatExtras;

/// <summary>In-memory HTTP: answers by URL from a route table and records every request.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<string> _requests = [];

    /// <summary>Exact URL → (status, body). Missing URLs answer 404.</summary>
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public int Count(string url) => Requests.Count(r => r == url);

    public void Json(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => Routes[url] = (status, body);

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (_gate) _requests.Add(url);
        var (status, body) = Routes.TryGetValue(url, out var route) ? route : (HttpStatusCode.NotFound, "{}");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("cuebot-chatextras-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Provider JSON as the public APIs return it (trimmed to the fields Cuebot reads).</summary>
internal static class ProviderJson
{
    public const string SevenTvGlobal = """
        {"id":"global","name":"Global Emotes","emotes":[
          {"id":"60ae958e229664e8667aea38","name":"peepoHappy","flags":0,
           "data":{"id":"60ae958e229664e8667aea38","name":"peepoHappy","flags":0,"animated":false,
                   "host":{"url":"//cdn.7tv.app/emote/60ae958e229664e8667aea38",
                           "files":[{"name":"1x.avif","format":"AVIF"},{"name":"1x.webp","format":"WEBP"},{"name":"1x.png","format":"PNG"},{"name":"2x.png","format":"PNG"}]}}},
          {"id":"6128ed55a50c52b1429e09dc","name":"RainTime","flags":1,
           "data":{"id":"6128ed55a50c52b1429e09dc","name":"RainTime","flags":256,"animated":true,
                   "host":{"url":"//cdn.7tv.app/emote/6128ed55a50c52b1429e09dc","files":[{"name":"1x.webp"},{"name":"1x.gif"}]}}}
        ]}
        """;

    public const string SevenTvChannel = """
        {"id":"123","platform":"TWITCH","username":"streamer",
         "emote_set":{"id":"set1","name":"streamer's set","emotes":[
           {"id":"aaa","name":"catJAM","flags":0,"data":{"id":"aaa","flags":0,"animated":true,"host":{"url":"//cdn.7tv.app/emote/aaa","files":[{"name":"1x.webp"}]}}},
           {"id":"bbb","name":"KEKW","flags":0,"data":{"id":"bbb","flags":0,"animated":false,"host":{"url":"//cdn.7tv.app/emote/bbb","files":[]}}}
         ]}}
        """;

    public const string BttvGlobal = """
        [{"id":"54fa925e01e468494b85b54d","code":"OhMyGoodness","imageType":"png","animated":false,"userId":"5561169bd6b9d206222a8c19"},
         {"id":"567b5b520e984428652809b6","code":"SoSnowy","imageType":"gif","animated":true,"userId":"5561169bd6b9d206222a8c19"}]
        """;

    public const string BttvChannel = """
        {"id":"u1","bots":[],"avatar":"",
         "channelEmotes":[{"id":"c1","code":"ourEmote","imageType":"png","animated":false,"userId":"u1"}],
         "sharedEmotes":[{"id":"s1","code":"KEKW","imageType":"png","animated":false,"user":{"id":"x","name":"someone"}},
                         {"id":"s2","code":"catJAM","imageType":"gif","animated":true,"user":{"id":"y","name":"other"}}]}
        """;

    public const string FfzGlobal = """
        {"default_sets":[3],
         "sets":{"3":{"id":3,"title":"Global Emotes","emoticons":[
                   {"id":25927,"name":"CatBag","urls":{"1":"https://cdn.frankerfacez.com/emote/25927/1","2":"https://cdn.frankerfacez.com/emote/25927/2"},"animated":null,"modifier":false}]},
                 "4330":{"id":4330,"title":"Not default","emoticons":[{"id":1,"name":"Hidden","urls":{"1":"//cdn.frankerfacez.com/emote/1/1"}}]}},
         "users":{"4330":["someone"]}}
        """;

    public const string FfzRoom = """
        {"room":{"_id":1,"set":999,"id":"streamer","twitch_id":123},
         "sets":{"999":{"id":999,"emoticons":[
           {"id":5,"name":"ffzChannel","urls":{"1":"//cdn.frankerfacez.com/emote/5/1"},"animated":{"1":"https://cdn.frankerfacez.com/emote/5/animated/1"}},
           {"id":6,"name":"ffzHat","urls":{"1":"https://cdn.frankerfacez.com/emote/6/1"},"modifier":true}]}}}
        """;

    public static void AllProviders(FakeHttp http, string twitchId = "123")
    {
        http.Json("https://7tv.io/v3/emote-sets/global", SevenTvGlobal);
        http.Json($"https://7tv.io/v3/users/twitch/{twitchId}", SevenTvChannel);
        http.Json("https://api.betterttv.net/3/cached/emotes/global", BttvGlobal);
        http.Json($"https://api.betterttv.net/3/cached/users/twitch/{twitchId}", BttvChannel);
        http.Json("https://api.frankerfacez.com/v1/set/global", FfzGlobal);
        http.Json($"https://api.frankerfacez.com/v1/room/id/{twitchId}", FfzRoom);
    }

    public static AppLog Log() => new();
}
