using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Actions;

namespace Cuebot.Webhooks.Outgoing;

/// <summary>A checked Discord webhook message body, ready to send.</summary>
/// <param name="Payload">The JSON body.</param>
/// <param name="ThreadId">The thread a Discohook export named, if any.</param>
/// <param name="HasComponents">The body has components, so the request needs <c>with_components=true</c>.</param>
public sealed record DiscordWebhookBody(JsonObject Payload, string? ThreadId, bool HasComponents);

/// <summary>
/// Turns the JSON typed into a webhook step into a Discord body. The text is parsed first and %variables% are filled in
/// string values afterwards, so a viewer's name with a quote in it cannot break or add to the JSON. Mentions are limited
/// to those the author wrote, so a variable holding "@everyone" cannot ping anyone.
/// </summary>
public static partial class DiscordWebhookJson
{
    private const int MaxContent = 2000, MaxEmbeds = 10, MaxEmbedTotal = 6000, MaxTitle = 256, MaxDescription = 4096,
        MaxFields = 25, MaxFieldName = 256, MaxFieldValue = 1024, MaxFooter = 2048, MaxAuthor = 256, MaxMentionIds = 100;

    /// <summary>What Discord accepts when editing a webhook message.</summary>
    private static readonly HashSet<string> EditableKeys = new(StringComparer.Ordinal)
    {
        "content", "embeds", "flags", "allowed_mentions", "components", "poll",
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>A body from Execute Webhook JSON or a Discohook export.</summary>
    public static DiscordWebhookBody FromJson(string template, Func<string, string> resolve, bool forEdit = false)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("Message JSON is required.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(template, documentOptions: ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The message JSON is not valid (line {(ex.LineNumber ?? 0) + 1}). " +
                                        "Put %variables% inside quotes, like \"title\": \"%user%\".");
        }
        if (root is not JsonObject top) throw new ArgumentException("The message JSON must be an object: { ... }.");
        var (data, threadId) = Unwrap(top);
        return Finish(data, threadId, resolve, forEdit);
    }

    /// <summary>A body with only message text, with the same protection for mentions.</summary>
    public static DiscordWebhookBody FromText(string template, Func<string, string> resolve, bool forEdit = false) =>
        Finish(new JsonObject { ["content"] = template }, null, resolve, forEdit);

    private static DiscordWebhookBody Finish(JsonObject data, string? threadId, Func<string, string> resolve, bool forEdit)
    {
        Normalize(data);
        if (forEdit)
            foreach (var key in data.Select(p => p.Key).Where(k => !EditableKeys.Contains(k)).ToArray()) data.Remove(key);

        var mentions = data.ContainsKey("allowed_mentions") ? null : AllowedMentions(data, resolve);
        Substitute(data, resolve);
        if (mentions is not null) data["allowed_mentions"] = mentions;
        NormalizeColors(data);
        Validate(data, forEdit);
        return new DiscordWebhookBody(data, threadId, data["components"] is JsonArray { Count: > 0 });
    }

    /// <summary>Discohook exports wrap the message: <c>{version, messages: [{data, thread_id?}]}</c>.</summary>
    private static (JsonObject Data, string? ThreadId) Unwrap(JsonObject top)
    {
        if (top["messages"] is JsonArray messages)
        {
            if (messages.Count != 1)
                throw new ArgumentException($"This Discohook export has {messages.Count} messages. A step sends one, so export them one at a time.");
            if (messages[0] is not JsonObject message || message["data"] is not JsonObject inner)
                throw new ArgumentException("This Discohook export has no message data.");
            var thread = Text(message["thread_id"]);
            message.Remove("data");
            return (inner, thread.Length > 0 ? thread : null);
        }
        if (top["data"] is JsonObject wrapped && !top.ContainsKey("content") && !top.ContainsKey("embeds"))
        {
            top.Remove("data");
            return (wrapped, null);
        }
        return (top, null);
    }

    /// <summary>Drops what only Discohook uses, and maps its older <c>author</c> onto the sender fields.</summary>
    private static void Normalize(JsonObject data)
    {
        data.Remove("webhook_id");
        data.Remove("attachments");   // no files are uploaded, and an empty list would strip them when editing
        if (data["author"] is JsonObject author)
        {
            if (Text(author["name"]) is { Length: > 0 } name && !data.ContainsKey("username")) data["username"] = name;
            if (Text(author["icon_url"]) is { Length: > 0 } icon && !data.ContainsKey("avatar_url")) data["avatar_url"] = icon;
        }
        data.Remove("author");
    }

    /// <summary>
    /// Only the mentions in the typed text may ping: @everyone/@here written there, and &lt;@id&gt; or &lt;@&amp;id&gt; whose id is
    /// typed or is a whole variable (&lt;@%discordUserId%&gt;, &lt;@~ownerId~&gt;). Read before variables are filled in.
    /// </summary>
    private static JsonObject AllowedMentions(JsonObject data, Func<string, string> resolve)
    {
        var typed = new List<string>();
        Strings(data["content"], typed);
        Strings(data["components"], typed);

        var users = new List<string>();
        var roles = new List<string>();
        var everyone = false;
        foreach (var text in typed)
        {
            everyone |= text.Contains("@everyone", StringComparison.Ordinal) || text.Contains("@here", StringComparison.Ordinal);
            foreach (Match m in MentionPattern().Matches(text))
            {
                var id = resolve(m.Groups["id"].Value).Trim();
                if (!IdPattern().IsMatch(id)) continue;
                var list = m.Groups["role"].Success ? roles : users;
                if (list.Count < MaxMentionIds && !list.Contains(id)) list.Add(id);
            }
        }

        var parse = new JsonArray();
        if (everyone) parse.Add("everyone");
        var result = new JsonObject { ["parse"] = parse };
        if (users.Count > 0) result["users"] = new JsonArray(users.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray());
        if (roles.Count > 0) result["roles"] = new JsonArray(roles.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return result;
    }

    private static void Strings(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, value) in o) Strings(value, into);
                break;
            case JsonArray a:
                foreach (var value in a) Strings(value, into);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                into.Add(v.GetValue<string>());
                break;
        }
    }

    private static void Substitute(JsonNode? node, Func<string, string> resolve)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToArray())
                {
                    if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String) o[key] = resolve(v.GetValue<string>());
                    else Substitute(o[key], resolve);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.String) a[i] = resolve(v.GetValue<string>());
                    else Substitute(a[i], resolve);
                }
                break;
        }
    }

    /// <summary>Embed colours may be written "#ff8800" or come from a variable; Discord wants a number.</summary>
    private static void NormalizeColors(JsonObject data)
    {
        if (data["embeds"] is not JsonArray embeds) return;
        foreach (var embed in embeds.OfType<JsonObject>())
        {
            if (embed["color"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String) continue;
            var text = value.GetValue<string>().Trim();
            if (text.Length == 0) embed.Remove("color");
            else embed["color"] = ParseColor(text);
        }
    }

    internal static int ParseColor(string text)
    {
        var hex = text.StartsWith('#') ? text[1..] : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : null;
        var ok = hex is not null
            ? int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var color)
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out color);
        if (!ok || color is < 0 or > 0xFFFFFF) throw new ArgumentException($"\"{text}\" is not an embed colour. Use one like #ff8800.");
        return color;
    }

    private static void Validate(JsonObject data, bool forEdit)
    {
        var content = Text(data["content"]);
        if (content.Length > MaxContent)
            throw new ArgumentException($"Discord messages are limited to {MaxContent} characters (got {content.Length}).");

        var embeds = data["embeds"] as JsonArray;
        if (data["embeds"] is not (null or JsonArray)) throw new ArgumentException("\"embeds\" must be a list: [ { ... } ].");
        if (embeds is { Count: > MaxEmbeds }) throw new ArgumentException($"A Discord message can have at most {MaxEmbeds} embeds (got {embeds.Count}).");
        var total = 0;
        foreach (var embed in embeds?.OfType<JsonObject>() ?? [])
        {
            total += Limit(embed["title"], MaxTitle, "An embed title");
            total += Limit(embed["description"], MaxDescription, "An embed description");
            total += Limit((embed["footer"] as JsonObject)?["text"], MaxFooter, "An embed footer");
            total += Limit((embed["author"] as JsonObject)?["name"], MaxAuthor, "An embed author name");
            if (embed["fields"] is not JsonArray fields) continue;
            if (fields.Count > MaxFields) throw new ArgumentException($"An embed can have at most {MaxFields} fields (got {fields.Count}).");
            foreach (var field in fields.OfType<JsonObject>())
            {
                total += Limit(field["name"], MaxFieldName, "An embed field name");
                total += Limit(field["value"], MaxFieldValue, "An embed field value");
            }
        }
        if (total > MaxEmbedTotal) throw new ArgumentException($"All embeds together are limited to {MaxEmbedTotal} characters (got {total}).");

        CheckComponents(data["components"]);

        if (forEdit)
        {
            if (data.Count == 0 || (data.Count == 1 && data.ContainsKey("allowed_mentions")))
                throw new ArgumentException("The message JSON has nothing to change.");
            return;
        }
        var hasBody = content.Trim().Length > 0 || embeds is { Count: > 0 } || data["components"] is JsonArray { Count: > 0 } || data["poll"] is JsonObject;
        if (!hasBody) throw new ArgumentException("The message needs content, an embed, a link button or a poll.");
    }

    private static int Limit(JsonNode? node, int max, string what)
    {
        var length = Text(node).Length;
        if (length > max) throw new ArgumentException($"{what} is limited to {max} characters (got {length}).");
        return length;
    }

    /// <summary>A normal webhook can only send link buttons; anything that needs a click handled belongs to a bot.</summary>
    private static void CheckComponents(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var child in a) CheckComponents(child);
                break;
            case JsonObject o:
                if (o["type"] is JsonValue t && t.TryGetValue<int>(out var type))
                {
                    if (type is >= 3 and <= 8)
                        throw new ArgumentException("Webhooks cannot send select menus or text inputs. Use link buttons, or the Discord bot.");
                    if (type == 2 && !(o["style"] is JsonValue s && s.TryGetValue<int>(out var style) && style == 5))
                        throw new ArgumentException("Webhooks can only send link buttons (style 5 with a url). Other buttons need the Discord bot.");
                }
                foreach (var (_, value) in o) CheckComponents(value);
                break;
        }
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue v
            ? v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : ""
            : "";

    /// <summary>
    /// The message a step points at: an id, "channel/message", a message link, or a name from "Remember it as". A
    /// remembered thread comes back with it, because Discord needs the thread to find a message inside one.
    /// </summary>
    public static (string MessageId, string? ThreadId) Target(string text, GlobalVariables globals)
    {
        var value = text.Trim();
        if (value.Length == 0) throw new ArgumentException("Say which message.");
        string? thread = null;
        for (var hop = 0; hop < 2; hop++)
        {
            var id = MessageIdOf(value);
            if (id is not null) return (id, thread);
            if (globals.Get(value) is not { Length: > 0 } remembered) break;
            thread = globals.Get(ThreadVariable(value));
            value = remembered.Trim();
        }
        throw new ArgumentException($"Cuebot does not know a message called \"{text.Trim()}\". Use the name you typed in \"Remember it as\", " +
                                    "a message id, or a message link.");
    }

    /// <summary>The global variable holding the thread of a message remembered as <paramref name="name"/>.</summary>
    public static string ThreadVariable(string name) => name.Trim() + " thread";

    private static string? MessageIdOf(string value)
    {
        if (LinkPattern().Match(value) is { Success: true } link) return link.Groups["m"].Value;
        if (PairPattern().Match(value) is { Success: true } pair) return pair.Groups["m"].Value;
        return IdPattern().IsMatch(value) ? value : null;
    }

    [GeneratedRegex(@"<@(?<role>&)?!?(?<id>\d+|%[^%\s>]+%|~[^~\s>]+~)>")]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"^\d{1,25}$")]
    internal static partial Regex IdPattern();

    [GeneratedRegex(@"discord(?:app)?\.com/channels/(?:\d+|@me)/\d+/(?<m>\d+)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^\d+\s*[/:]\s*(?<m>\d+)$")]
    private static partial Regex PairPattern();
}
