using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;

namespace Cuebot.Core.Overlays.Events;

/// <summary>
/// Builds StreamElements' chat "message" event from a combined-chat message, for every platform:
/// <c>{data: {time, tags, nick, userId, displayName, displayColor, channel, text, isAction, msgId, badges[], emotes[]},
/// renderedText, service}</c>. Kick's "[emote:id:name]" tokens become the emote's name, so <c>text</c>, emote ranges
/// and <c>renderedText</c> agree on every platform. Twitch-style <c>tags</c> are filled for all platforms, so widgets
/// that read <c>tags.mod</c> or <c>tags.subscriber</c> work everywhere. Cuebot adds <c>data.avatar</c> (when the
/// platform reports a picture) and <c>data.pronouns</c> (Twitch chatters with pronouns set on alejo.io, once looked up).
/// </summary>
public static class OverlayChatMessage
{
    /// <summary>Twitch's default name colours, used for chatters whose platform reports none.</summary>
    private static readonly string[] DefaultColors =
    [
        "#FF0000", "#0000FF", "#008000", "#B22222", "#FF7F50", "#9ACD32", "#FF4500", "#2E8B57",
        "#DAA520", "#D2691E", "#5F9EA0", "#1E90FF", "#FF69B4", "#8A2BE2", "#00FF7F",
    ];

    /// <param name="args">The "chat.message" event's arguments.</param>
    /// <param name="platformArgs">The platform event's arguments ("platformArgs" of "chat.message").</param>
    public static JsonObject Build(IReadOnlyDictionary<string, object?> args, IReadOnlyDictionary<string, object?> platformArgs,
        IReadOnlyList<EmoteMatch> emotes, IReadOnlyList<OverlayBadge> badges, string service, DateTimeOffset time)
    {
        var platform = OverlayAlerts.Str(args, "platform");
        var user = OverlayAlerts.Str(args, "user");
        var userId = OverlayAlerts.Str(args, "userId");
        var msgId = OverlayAlerts.Str(args, "messageId");
        var login = OverlayAlerts.First(platformArgs, "userName", "userSlug");
        var nick = (login.Length > 0 ? login : user).ToLowerInvariant();
        var color = OverlayAlerts.Str(platformArgs, "color");
        if (!IsHexColor(color)) color = DefaultColor(userId.Length > 0 ? userId : user);

        var (text, ranges) = Render(OverlayAlerts.Str(args, "message"), emotes);
        var isMod = OverlayAlerts.Bool(args, "isModerator");
        var isSub = OverlayAlerts.Bool(args, "isSubscriber");
        var isBroadcaster = OverlayAlerts.Bool(args, "isBroadcaster");
        var isVip = OverlayAlerts.Bool(args, "isVip");
        var firstMessage = OverlayAlerts.Bool(platformArgs, "isFirstMessage") || OverlayAlerts.Bool(platformArgs, "firstMessage");
        var badgeTags = string.Join(",", badges.Select(b => $"{b.Type}/{b.Version}"));
        var timeMs = time.ToUnixTimeMilliseconds();

        var tags = new JsonObject
        {
            ["badge-info"] = "",
            ["badges"] = badgeTags,
            ["color"] = color,
            ["display-name"] = user,
            ["emotes"] = platform.Equals(EmoteProviders.Twitch, StringComparison.OrdinalIgnoreCase) ? OverlayAlerts.Str(platformArgs, "emotes") : "",
            ["first-msg"] = firstMessage ? "1" : "0",
            ["id"] = msgId,
            ["mod"] = isMod ? "1" : "0",
            ["subscriber"] = isSub ? "1" : "0",
            ["vip"] = isVip ? "1" : "0",
            ["broadcaster"] = isBroadcaster ? "1" : "0",
            ["turbo"] = "0",
            ["tmi-sent-ts"] = timeMs.ToString(CultureInfo.InvariantCulture),
            ["user-id"] = userId,
            ["user-type"] = isMod ? "mod" : "",
        };

        var badgeArray = new JsonArray();
        foreach (var b in badges)
            badgeArray.Add(new JsonObject { ["type"] = b.Type, ["version"] = b.Version, ["url"] = b.Url, ["description"] = b.Description });

        var emoteArray = new JsonArray();
        foreach (var (match, start, end) in ranges)
        {
            var e = match.Emote;
            emoteArray.Add(new JsonObject
            {
                ["type"] = e.Provider.ToLowerInvariant(),
                ["name"] = e.Code,
                ["id"] = e.Id,
                ["gif"] = e.IsAnimated,
                ["urls"] = new JsonObject { ["1"] = e.ImageUrl, ["2"] = e.ImageUrl, ["4"] = e.ImageUrl },
                ["start"] = start,
                ["end"] = end,
            });
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["time"] = timeMs,
                ["tags"] = tags,
                ["nick"] = nick,
                ["userId"] = userId,
                ["displayName"] = user,
                ["displayColor"] = color,
                ["channel"] = "",
                ["text"] = text,
                ["isAction"] = false,
                ["msgId"] = msgId,
                ["badges"] = badgeArray,
                ["emotes"] = emoteArray,
                ["avatar"] = OverlayAlerts.First(platformArgs, "userProfileUrl", "userProfilePicture", "profileImageUrl"),
                ["pronouns"] = OverlayAlerts.Str(args, "pronouns"),
            },
            ["renderedText"] = RenderHtml(text, ranges),
            ["service"] = service,
        };
    }

    /// <summary>
    /// The message text with every emote match replaced by its code (only Kick's tokens differ), and each emote's
    /// UTF-16 start and inclusive end in that text.
    /// </summary>
    internal static (string Text, IReadOnlyList<(EmoteMatch Match, int Start, int End)> Ranges) Render(string message, IReadOnlyList<EmoteMatch> emotes)
    {
        var text = new StringBuilder(message.Length);
        var ranges = new List<(EmoteMatch, int, int)>();
        var at = 0;
        foreach (var match in emotes.Where(m => m.Start >= 0 && m.End <= message.Length).OrderBy(m => m.Start))
        {
            if (match.Start < at) continue;   // overlapping matches: the first wins
            text.Append(message, at, match.Start - at);
            var start = text.Length;
            text.Append(match.Emote.Code);
            ranges.Add((match, start, text.Length - 1));
            at = match.End;
        }
        text.Append(message, at, message.Length - at);
        return (text.ToString(), ranges);
    }

    /// <summary>HTML for the text with emotes as images, like StreamElements' <c>renderedText</c>.</summary>
    internal static string RenderHtml(string text, IReadOnlyList<(EmoteMatch Match, int Start, int End)> ranges)
    {
        var html = new StringBuilder();
        var at = 0;
        foreach (var (match, start, end) in ranges)
        {
            html.Append(WebUtility.HtmlEncode(text[at..start]));
            var e = match.Emote;
            var code = WebUtility.HtmlEncode(e.Code);
            html.Append("<img src=\"").Append(WebUtility.HtmlEncode(e.ImageUrl)).Append("\" title=\"").Append(code)
                .Append("\" alt=\"").Append(code).Append("\" class=\"emote\">");
            at = end + 1;
        }
        html.Append(WebUtility.HtmlEncode(text[at..]));
        return html.ToString();
    }

    private static bool IsHexColor(string color) =>
        color.Length == 7 && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>A stable colour for a chatter (the same id always gets the same colour).</summary>
    internal static string DefaultColor(string key)
    {
        var hash = 0u;
        foreach (var c in key) hash = unchecked(hash * 31 + c);
        return DefaultColors[hash % (uint)DefaultColors.Length];
    }
}
