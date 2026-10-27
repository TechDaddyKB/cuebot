using System.Text.Json;

namespace Cuebot.Rumble.Chat;

/// <summary>A chat message from the live chat stream, with its author resolved.</summary>
internal sealed record ChatStreamMessage(
    string Id,
    string UserId,
    string UserName,
    string Text,
    DateTimeOffset Time,
    IReadOnlyList<string> Badges,
    string? Color,
    long RantCents)
{
    public bool IsRant => RantCents > 0;
}

/// <summary>
/// A decoded chat stream event. "init" (sent on every connect, with recent history) and "messages" carry
/// messages; other types (deletions, pins, ...) carry none and are only reported by type.
/// </summary>
internal sealed record ChatStreamBatch(string Type, IReadOnlyList<ChatStreamMessage> Messages)
{
    public const string Init = "init";
}

/// <summary>
/// Decodes the JSON of Rumble chat stream events: <c>{"type": "...", "data": {"messages": [...], "users": [...],
/// "channels": [...]}}</c>. Messages refer to their author by <c>user_id</c> (and <c>channel_id</c> when posted as a
/// channel); authors are sent alongside, once, so the decoder remembers them for the life of a chat session.
/// </summary>
internal sealed class ChatStreamDecoder
{
    private readonly Dictionary<string, Author> _users = [];
    private readonly Dictionary<string, Author> _channels = [];

    /// <summary>Returns null when <paramref name="json"/> is not a chat event.</summary>
    public ChatStreamBatch? Decode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.Str("type");
            if (type.Length == 0) return null;

            var data = root.Child("data") ?? default;
            Remember(_users, data.Items("users"));
            Remember(_channels, data.Items("channels"));
            return new ChatStreamBatch(type, data.Items("messages").Select(ToMessage).Where(m => m.Text.Length > 0).ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Remember(Dictionary<string, Author> authors, IEnumerable<JsonElement> items)
    {
        foreach (var item in items)
        {
            var id = item.Str("id");
            if (id.Length == 0) continue;
            var color = item.Str("color");
            authors[id] = new Author(item.Str("username"), color.Length > 0 ? color : null, item.Strings("badges"));
        }
    }

    private ChatStreamMessage ToMessage(JsonElement message)
    {
        var userId = message.Str("user_id");
        var user = _users.GetValueOrDefault(userId);
        // A user can post as one of their channels; the channel's name is what chat shows.
        var channel = _channels.GetValueOrDefault(message.Str("channel_id"));
        var rant = message.Child("rant") ?? default;
        return new ChatStreamMessage(
            message.Str("id"),
            userId,
            channel?.UserName ?? user?.UserName ?? "",
            message.Str("text"),
            message.Time("time"),
            user?.Badges ?? [],
            user?.Color,
            rant.Long("price_cents"));
    }

    private sealed record Author(string UserName, string? Color, IReadOnlyList<string> Badges);
}
