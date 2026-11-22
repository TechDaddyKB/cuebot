using Cuebot.Core.Actions;
using Cuebot.Core.Storage;

namespace Cuebot.Webhooks.Outgoing;

/// <summary>A sub-action that calls an outgoing webhook (Discord, IFTTT).</summary>
internal sealed class OutgoingWebhookSubAction(string typeId, string category, string displayName,
    IReadOnlyList<ParameterDescriptor> parameters, Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await run(context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>The outgoing webhook sub-actions by type id (built once, registered one by one).</summary>
internal sealed class OutgoingSubActionSet(IReadOnlyList<ISubActionHandler> handlers)
{
    private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

    public ISubActionHandler Get(string typeId) => _byId[typeId];
}

/// <summary>
/// Streamer.bot's Discord "Basic Webhook" and IFTTT "Notification (Basic)" / "(Advanced)" sub-actions.
/// Type ids are stored in actions.json and must never change.
/// </summary>
public static class OutgoingWebhookSubActions
{
    public const string DiscordBasicWebhook = "discord.basicWebhook";
    public const string DiscordEditWebhookMessage = "discord.editWebhookMessage";
    public const string DiscordDeleteWebhookMessage = "discord.deleteWebhookMessage";
    public const string IftttNotificationBasic = "ifttt.notificationBasic";
    public const string IftttNotificationAdvanced = "ifttt.notificationAdvanced";

    /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
    public static readonly string[] TypeIds =
    [
        DiscordBasicWebhook, DiscordEditWebhookMessage, DiscordDeleteWebhookMessage, IftttNotificationBasic, IftttNotificationAdvanced,
    ];

    private const string SimpleBody = "Simple", JsonBody = "JSON";

    public static IReadOnlyList<ISubActionHandler> Create(DiscordWebhookClient discord, IftttWebhookClient ifttt,
        JsonFile<WebhooksSettings> settings)
    {
        ParameterDescriptor IftttKey() => new("key", "IFTTT Webhooks key")
        {
            Help = "Leave empty to use the key saved in webhooks.json (IftttKey). From ifttt.com/maker_webhooks → Documentation",
        };
        string Key(ParameterValues v) => v["key"].Trim() is { Length: > 0 } key ? key : settings.Value.IftttKey;

        return
        [
            new OutgoingWebhookSubAction(DiscordBasicWebhook, "Discord", "Basic webhook",
                [
                    new("name", "Webhook name") { Help = "A label for this webhook (not sent)" },
                    new("webhookUrl", "Webhook URL") { Help = "Discord: Server Settings → Integrations → Webhooks → Copy Webhook URL" },
                    new("content", "Content", ParameterKind.Multiline)
                    {
                        Help = "Ping users with <@USER_ID> and roles with <@&ROLE_ID>. Only pings typed here work; text from a variable cannot ping",
                        ResolveVariables = false,
                    },
                    new("username", "Username") { Help = "Leave empty for the webhook's own name" },
                    new("avatarUrl", "Avatar URL") { Help = "Leave empty for the webhook's own avatar" },
                    new("textToSpeech", "Text to speech", ParameterKind.Boolean) { DefaultValue = "false" },
                    BodyMode(),
                    MessageJson(),
                    new("threadId", "Thread ID") { Help = "Optional. Posts into this thread or forum post" },
                    new("threadName", "New forum post title") { Help = "Optional. For forum channels: starts a new post with this title" },
                    new("rememberAs", "Remember it as") { Help = "A name like \"live post\", so a later step can edit or delete this message" },
                ],
                async (ctx, v, ct) =>
                {
                    var body = IsJson(v)
                        ? DiscordWebhookJson.FromJson(v["json"], ctx.Resolve)
                        : DiscordWebhookJson.FromText(v["content"], ctx.Resolve);
                    // The step's sender fields fill in for both kinds of body; a name inside the JSON wins.
                    if (v["username"].Trim() is { Length: > 0 } username && !body.Payload.ContainsKey("username")) body.Payload["username"] = username;
                    if (v["avatarUrl"].Trim() is { Length: > 0 } avatar && !body.Payload.ContainsKey("avatar_url")) body.Payload["avatar_url"] = avatar;
                    if (!IsJson(v) || IsTrue(v["textToSpeech"])) body.Payload["tts"] = IsTrue(v["textToSpeech"]);

                    var sent = await discord.PostAsync(v["webhookUrl"], body, v["threadId"], v["threadName"], ct).ConfigureAwait(false);
                    // %messageId% for Streamer.bot actions (it replaces the trigger's chat message id), and a name of its own.
                    ctx.Args["messageId"] = sent.MessageId;
                    ctx.Args["discordMessageId"] = sent.MessageId;
                    ctx.Args["discordChannelId"] = sent.ChannelId;
                    ctx.Args["discordThreadId"] = sent.InThread ? sent.ChannelId : "";
                    if (v["rememberAs"].Trim() is { Length: > 0 } name && sent.MessageId.Length > 0)
                    {
                        ctx.Globals.Set(name, $"{sent.ChannelId}/{sent.MessageId}", persist: true);
                        if (sent.InThread) ctx.Globals.Set(DiscordWebhookJson.ThreadVariable(name), sent.ChannelId, persist: true);
                        else ctx.Globals.Remove(DiscordWebhookJson.ThreadVariable(name));
                    }
                }),
            new OutgoingWebhookSubAction(DiscordEditWebhookMessage, "Discord", "Edit webhook message",
                [
                    new("webhookUrl", "Webhook URL") { Help = "The webhook that sent the message" },
                    WebhookMessage(),
                    BodyMode(),
                    new("content", "Content", ParameterKind.Multiline)
                    {
                        Help = "The new text; used when Message body is Simple",
                        ResolveVariables = false,
                    },
                    MessageJson(),
                    new("threadId", "Thread ID") { Help = "Needed when the message is in a thread, unless it was remembered by name" },
                ],
                async (ctx, v, ct) =>
                {
                    var (messageId, rememberedThread) = DiscordWebhookJson.Target(v["message"], ctx.Globals);
                    var body = IsJson(v)
                        ? DiscordWebhookJson.FromJson(v["json"], ctx.Resolve, forEdit: true)
                        : DiscordWebhookJson.FromText(v["content"], ctx.Resolve, forEdit: true);
                    var thread = v["threadId"].Trim() is { Length: > 0 } typed ? typed : rememberedThread;
                    var edited = await discord.EditAsync(v["webhookUrl"], messageId, body, thread, ct).ConfigureAwait(false);
                    ctx.Args["discordMessageId"] = edited.MessageId;
                    ctx.Args["discordChannelId"] = edited.ChannelId;
                }),
            new OutgoingWebhookSubAction(DiscordDeleteWebhookMessage, "Discord", "Delete webhook message",
                [
                    new("webhookUrl", "Webhook URL") { Help = "The webhook that sent the message" },
                    WebhookMessage(),
                    new("threadId", "Thread ID") { Help = "Needed when the message is in a thread, unless it was remembered by name" },
                ],
                async (ctx, v, ct) =>
                {
                    var (messageId, rememberedThread) = DiscordWebhookJson.Target(v["message"], ctx.Globals);
                    var thread = v["threadId"].Trim() is { Length: > 0 } typed ? typed : rememberedThread;
                    await discord.DeleteAsync(v["webhookUrl"], messageId, thread, ct).ConfigureAwait(false);
                }),
            new OutgoingWebhookSubAction(IftttNotificationBasic, "IFTTT", "Notification (basic)",
                [
                    new("eventName", "Event name") { Help = "The event name of your applet's Webhooks trigger" },
                    new("text", "Text", ParameterKind.Multiline) { Help = "Sent as value1" },
                    IftttKey(),
                ],
                (_, v, ct) => ifttt.NotifyAsync(Key(v), v["eventName"], v["text"], ct)),
            new OutgoingWebhookSubAction(IftttNotificationAdvanced, "IFTTT", "Notification (advanced)",
                [
                    new("eventName", "Event name") { Help = "The event name of your applet's Webhooks (JSON) trigger" },
                    new("arguments", "Values (one name=value per line)", ParameterKind.Multiline)
                    {
                        Help = "The first five become Argument1Name/Argument1Value...; all of them are in ArgumentsJson",
                    },
                    IftttKey(),
                ],
                (_, v, ct) => ifttt.NotifyAdvancedAsync(Key(v), v["eventName"], IftttWebhookClient.ParseArguments(v["arguments"]), ct)),
        ];
    }

    private static ParameterDescriptor BodyMode() => new("bodyMode", "Message body", ParameterKind.Choice)
    {
        DefaultValue = SimpleBody,
        Choices = [SimpleBody, JsonBody],
        Help = "Simple sends the Content text. JSON sends the Message JSON, for embeds and link buttons",
    };

    private static ParameterDescriptor MessageJson() => new("json", "Message JSON", ParameterKind.Multiline)
    {
        Help = "Used when Message body is JSON. Paste Discord webhook JSON or a Discohook export. %variables% go inside quotes and are filled in safely",
        ResolveVariables = false,
    };

    private static ParameterDescriptor WebhookMessage() => new("message", "Message")
    {
        DefaultValue = "%discordMessageId%",
        Help = "The name from \"Remember it as\", a message id, or a message link",
    };

    private static bool IsJson(ParameterValues v) => string.Equals(v["bodyMode"], JsonBody, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string value) =>
        bool.TryParse(value.Trim(), out var b) ? b : value.Trim() is "1" or "yes" or "on";
}
