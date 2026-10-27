using System.Text.Json;

namespace Cuebot.YouTube.Tests.Support;

/// <summary>Builders for liveChatMessages resources and pages, shaped like the YouTube Data API's.</summary>
public static class ChatJson
{
    public const string Owner = "UCownerownerownerowner01";
    public const string Alice = "UCaliceAliceAliceAlice01";
    public const string Bob = "UCbobBobBobBobBobBobBo01";

    public static string Author(string channelId, string name, bool owner = false, bool moderator = false, bool member = false,
        bool verified = false) =>
        $$"""
        {"channelId":"{{channelId}}","channelUrl":"http://www.youtube.com/channel/{{channelId}}","displayName":"{{name}}",
         "profileImageUrl":"https://yt3.test/{{channelId}}.jpg","isVerified":{{B(verified)}},"isChatOwner":{{B(owner)}},
         "isChatSponsor":{{B(member)}},"isChatModerator":{{B(moderator)}}}
        """;

    public static string Item(string id, string type, string details, string? author = null) =>
        $$"""
        {"kind":"youtube#liveChatMessage","id":"{{id}}",
         "snippet":{"type":"{{type}}","liveChatId":"chat1","authorChannelId":"{{Alice}}","publishedAt":"2026-03-10T18:00:00Z",
                    "hasDisplayContent":true,"displayMessage":"display {{id}}"{{(details.Length > 0 ? "," + details : "")}}},
         "authorDetails":{{author ?? Author(Alice, "Alice")}}}
        """;

    public static string Text(string id, string text, string? author = null) =>
        Item(id, "textMessageEvent", $$"""
            "textMessageDetails":{"messageText":{{JsonSerializer.Serialize(text)}}}
            """, author);

    public static string Page(string nextPageToken, int pollingMillis, params string[] items) =>
        $$"""
        {"kind":"youtube#liveChatMessageListResponse","nextPageToken":"{{nextPageToken}}","pollingIntervalMillis":{{pollingMillis}},
         "pageInfo":{"totalResults":{{items.Length}},"resultsPerPage":{{items.Length}}},"items":[{{string.Join(",", items)}}]}
        """;

    public static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string B(bool value) => value ? "true" : "false";
}
