using Cuebot.Core.Events;

namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Collects chat replies.</summary>
internal sealed class FakeChatReplier : ISpeechChatReplier
{
    private readonly List<(string Platform, string Text)> _replies = [];

    public IReadOnlyList<(string Platform, string Text)> Replies
    {
        get
        {
            lock (_replies) return _replies.ToArray();
        }
    }

    public string Last => Replies.Count == 0 ? "" : Replies[^1].Text;

    public Task ReplyAsync(string platform, string text, CancellationToken ct)
    {
        lock (_replies) _replies.Add((platform, text));
        return Task.CompletedTask;
    }
}

/// <summary>Builds "chat.message" and "chat.activity" events the way ChatHub publishes them.</summary>
internal static class ChatEvents
{
    public static BotEvent Message(string user, string message, string platform = "Twitch", string? userId = null,
        bool mod = false, bool broadcaster = false, bool vip = false, bool sub = false, string? messageId = null,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["user"] = user,
            ["userId"] = userId ?? user.ToLowerInvariant() + "-id",
            ["message"] = message,
            ["rawInput"] = message,
            ["messageId"] = messageId ?? Guid.NewGuid().ToString("N"),
            ["isBroadcaster"] = broadcaster,
            ["isModerator"] = mod,
            ["isVip"] = vip,
            ["isSubscriber"] = sub,
            ["amount"] = "",
        };
        if (extra is not null)
            foreach (var (key, value) in extra) args[key] = value;
        return new BotEvent("chat.message", args);
    }

    public static BotEvent Activity(string kind, string user, string title, string detail = "", string amount = "",
        string platform = "Twitch", IReadOnlyDictionary<string, object?>? extra = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["kind"] = kind,
            ["user"] = user,
            ["title"] = title,
            ["detail"] = detail,
            ["amount"] = amount,
        };
        if (extra is not null)
            foreach (var (key, value) in extra) args[key] = value;
        return new BotEvent("chat.activity", args);
    }
}
