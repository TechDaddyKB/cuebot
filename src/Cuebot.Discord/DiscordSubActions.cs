using Cuebot.Core.Actions;
using Cuebot.Discord.Gateway;

namespace Cuebot.Discord;

/// <summary>A sub-action built from a table entry.</summary>
internal sealed class DiscordSubAction(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => DiscordSubActions.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
    {
        await run(context, values, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>The bot's sub-actions. Type ids are stored in actions.json and must never change.</summary>
public static class DiscordSubActions
{
    public const string Category = "Discord";

    public const string SendMessage = "discord.sendMessage";
    public const string EditMessage = "discord.editMessage";
    public const string DeleteMessage = "discord.deleteMessage";
    public const string AddRole = "discord.addRole";
    public const string RemoveRole = "discord.removeRole";
    public const string ReplyToSlashCommand = "discord.replyToSlashCommand";
    public const string SendDirectMessage = "discord.sendDirectMessage";
    public const string SetStatus = "discord.setStatus";

    /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
    public static readonly string[] TypeIds =
        [SendMessage, EditMessage, DeleteMessage, AddRole, RemoveRole, ReplyToSlashCommand, SendDirectMessage, SetStatus];

    internal static IReadOnlyList<ISubActionHandler> Create(DiscordBotService bot)
    {
        ParameterDescriptor Channel() => new("channel", "Channel")
        {
            Suggestions = () => DiscordTargets.ChannelSuggestions(bot),
            Help = "Pick a channel from the list (the bot's servers), or type its name like #announcements or its ID",
        };
        ParameterDescriptor Text(string label = "Text") => new("text", label, ParameterKind.Multiline) { Help = "Up to 2000 characters. %variables% work here" };
        ParameterDescriptor[] Embed() =>
        [
            new("embedTitle", "Embed title") { Help = "Leave all embed boxes empty for a plain message" },
            new("embedDescription", "Embed text", ParameterKind.Multiline),
            new("embedUrl", "Embed link") { Help = "The title becomes a link to this address, like your stream" },
            new("embedColor", "Embed colour") { Help = "Like #9146FF, or a word such as twitch, youtube, red" },
            new("embedImage", "Embed image URL") { Help = "A web link to a picture, like your stream thumbnail" },
        ];
        DiscordEmbed? EmbedOf(ParameterValues v)
        {
            var e = new DiscordEmbed(v["embedTitle"].Trim(), v["embedDescription"].Trim(), v["embedUrl"].Trim(), v["embedColor"].Trim(), v["embedImage"].Trim());
            if (e.IsEmpty && e.Color.Length == 0) return null;
            if (e.IsEmpty) throw new ArgumentException("An embed needs a title, text, link or image as well as a colour.");
            return e;
        }
        ParameterDescriptor Message() => new("message", "Message")
        {
            DefaultValue = "%discordMessageLink%",
            Help = "The name you gave it with \"Remember it as\", or a message link",
        };
        ParameterDescriptor Member() => new("member", "Member") { Help = "Their user ID, or their name in the server. %userId% from a Discord trigger works" };
        ParameterDescriptor Role() => new("role", "Role")
        {
            Suggestions = () => DiscordTargets.RoleSuggestions(bot),
            Help = "Pick the role from the list, or type its name",
        };
        ParameterDescriptor Reason() => new("reason", "Reason") { Help = "Shown in the server's audit log. Can be left empty" };

        return
        [
            new DiscordSubAction(SendMessage, "Send message (bot)", [Channel(), Text(), .. Embed(),
                    new("rememberAs", "Remember it as") { Help = "A name like \"live post\", so a later step can edit or delete this message" }],
                async (ctx, v, ct) =>
                {
                    var channel = await DiscordTargets.ChannelAsync(bot, v["channel"], ct).ConfigureAwait(false);
                    var embed = EmbedOf(v);
                    if (v["text"].Trim().Length == 0 && embed is null) throw new ArgumentException("Type the message text or fill in the embed.");
                    var id = await bot.Api.CreateMessageAsync(channel.Id, v["text"], embed, ct).ConfigureAwait(false);
                    var link = DiscordBotService.MessageLink(channel.GuildId, channel.Id, id);
                    ctx.Args["discordMessageId"] = id;
                    ctx.Args["discordChannelId"] = channel.Id;
                    ctx.Args["discordMessageLink"] = link;
                    if (v["rememberAs"].Trim() is { Length: > 0 } name) ctx.Globals.Set(name, link, persist: true);
                }),
            new DiscordSubAction(EditMessage, "Edit message (bot)", [Message(), Text("New text"), .. Embed(),
                    new("removeEmbed", "Remove the embed", ParameterKind.Boolean) { DefaultValue = "false" }],
                async (ctx, v, ct) =>
                {
                    var (channelId, messageId) = DiscordTargets.Message(v["message"], ctx.Globals);
                    var text = v["text"].Trim().Length == 0 ? null : v["text"];
                    var embed = EmbedOf(v);
                    if (text is null && embed is null && !IsTrue(v["removeEmbed"])) throw new ArgumentException("Type the new text or fill in the embed.");
                    await bot.Api.EditMessageAsync(channelId, messageId, text, embed, IsTrue(v["removeEmbed"]), ct).ConfigureAwait(false);
                }),
            new DiscordSubAction(DeleteMessage, "Delete message (bot)", [Message()],
                async (ctx, v, ct) =>
                {
                    var (channelId, messageId) = DiscordTargets.Message(v["message"], ctx.Globals);
                    await bot.Api.DeleteMessageAsync(channelId, messageId, ct).ConfigureAwait(false);
                }),
            new DiscordSubAction(AddRole, "Give a role", [Member(), Role(), Reason()],
                async (_, v, ct) =>
                {
                    var role = await DiscordTargets.RoleAsync(bot, v["role"], ct).ConfigureAwait(false);
                    var member = await DiscordTargets.MemberAsync(bot, v["member"], role.GuildId, ct).ConfigureAwait(false);
                    await bot.Api.AddRoleAsync(role.GuildId, member.Id, role.Id, v["reason"], ct).ConfigureAwait(false);
                }),
            new DiscordSubAction(RemoveRole, "Take away a role", [Member(), Role(), Reason()],
                async (_, v, ct) =>
                {
                    var role = await DiscordTargets.RoleAsync(bot, v["role"], ct).ConfigureAwait(false);
                    var member = await DiscordTargets.MemberAsync(bot, v["member"], role.GuildId, ct).ConfigureAwait(false);
                    await bot.Api.RemoveRoleAsync(role.GuildId, member.Id, role.Id, v["reason"], ct).ConfigureAwait(false);
                }),
            new DiscordSubAction(ReplyToSlashCommand, "Reply to slash command", [Text("Answer"), .. Embed(),
                    new("interaction", "Slash command use") { DefaultValue = $"%{DiscordEventTypes.InteractionIdArg}%", Help = "Leave it as it is" }],
                (_, v, ct) => bot.ReplyAsync(v["interaction"], v["text"], EmbedOf(v), ct)),
            new DiscordSubAction(SendDirectMessage, "Send a direct message", [Member(), Text()],
                async (_, v, ct) =>
                {
                    if (v["text"].Trim().Length == 0) throw new ArgumentException("Type the message text.");
                    var member = await DiscordTargets.MemberAsync(bot, v["member"], null, ct).ConfigureAwait(false);
                    var channel = await bot.Api.CreateDmAsync(member.Id, ct).ConfigureAwait(false);
                    await bot.Api.CreateMessageAsync(channel, v["text"], null, ct).ConfigureAwait(false);
                }),
            new DiscordSubAction(SetStatus, "Set the bot's status",
                [
                    new("status", "Status", ParameterKind.Choice)
                    {
                        DefaultValue = "online", Choices = ["online", "idle", "dnd", "invisible"],
                        ChoiceLabels = new Dictionary<string, string> { ["online"] = "Online", ["idle"] = "Away", ["dnd"] = "Do not disturb", ["invisible"] = "Invisible" },
                    },
                    new("activity", "Activity", ParameterKind.Choice)
                    {
                        DefaultValue = "Watching", Choices = ["None", "Playing", "Streaming", "Listening", "Watching", "Competing", "Custom"],
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["None"] = "No activity", ["Playing"] = "Playing …", ["Streaming"] = "Streaming …", ["Listening"] = "Listening to …",
                            ["Watching"] = "Watching …", ["Competing"] = "Competing in …", ["Custom"] = "Just the text",
                        },
                    },
                    new("text", "Activity text") { Help = "Like \"my stream\"" },
                    new("streamUrl", "Stream link") { Help = "Only for Streaming: your Twitch or YouTube link" },
                ],
                (_, v, ct) => bot.SetPresenceAsync(v["status"].Trim().ToLowerInvariant() is "idle" or "dnd" or "invisible" ? v["status"].Trim().ToLowerInvariant() : "online",
                    v["activity"].Trim() switch
                    {
                        "Playing" => ActivityTypes.Playing, "Streaming" => ActivityTypes.Streaming, "Listening" => ActivityTypes.Listening,
                        "Watching" => ActivityTypes.Watching, "Competing" => ActivityTypes.Competing, "Custom" => ActivityTypes.Custom,
                        _ => null,
                    }, v["text"].Trim(), v["streamUrl"], ct)),
        ];
    }

    private static bool IsTrue(string value) =>
        bool.TryParse(value.Trim(), out var b) ? b : value.Trim() is "1" or "yes" or "on";
}
