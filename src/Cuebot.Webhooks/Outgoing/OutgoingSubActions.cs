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
    public const string IftttNotificationBasic = "ifttt.notificationBasic";
    public const string IftttNotificationAdvanced = "ifttt.notificationAdvanced";

    /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
    public static readonly string[] TypeIds = [DiscordBasicWebhook, IftttNotificationBasic, IftttNotificationAdvanced];

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
                    new("content", "Content", ParameterKind.Multiline) { Help = "Ping users with <@USER_ID> and roles with <@&ROLE_ID>" },
                    new("username", "Username") { Help = "Leave empty for the webhook's own name" },
                    new("avatarUrl", "Avatar URL") { Help = "Leave empty for the webhook's own avatar" },
                    new("textToSpeech", "Text to speech", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                async (ctx, v, ct) =>
                {
                    var id = await discord.PostAsync(v["webhookUrl"], v["content"], v["username"], v["avatarUrl"],
                        IsTrue(v["textToSpeech"]), ct).ConfigureAwait(false);
                    ctx.Args["messageId"] = id;
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

    private static bool IsTrue(string value) =>
        bool.TryParse(value.Trim(), out var b) ? b : value.Trim() is "1" or "yes" or "on";
}
