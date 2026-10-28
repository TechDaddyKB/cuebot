using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Variables;
using Cuebot.Kick.Triggers;

namespace Cuebot.Kick.SubActions;

/// <summary>Shared shape of the Kick sub-actions.</summary>
internal abstract class KickSubAction(KickService kick) : ISubActionHandler
{
    protected KickService Kick => kick;
    public abstract string TypeId { get; }
    public string Category => KickTriggers.Category;
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public virtual IReadOnlyList<string> Branches => [];
    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct);

    protected static ParameterDescriptor UseBot { get; } =
        new("useBot", "Send as bot account", ParameterKind.Boolean) { DefaultValue = "false" };

    protected static ParameterDescriptor Fallback { get; } =
        new("fallback", "Use the broadcaster when the bot is not signed in", ParameterKind.Boolean) { DefaultValue = "true" };

    protected static ParameterDescriptor User(string label = "User") =>
        new("user", label) { DefaultValue = "%userName%", Help = "Kick user name or numeric user id" };

    /// <summary>
    /// Streamer.bot's moderation outputs: {prefix} and {prefix}Name (the user's name), {prefix}Id, {prefix}Type and
    /// {result} (true; a failure stops the action instead). The name comes from the parameter, or from the trigger
    /// when the parameter is the triggering user's id.
    /// </summary>
    protected async Task WriteTargetAsync(ActionRunContext context, string user, string prefix, string result, CancellationToken ct)
    {
        string Arg(string key) => context.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        var id = Events.KickArgs.Id(await Kick.ResolveUserIdAsync(user, ct).ConfigureAwait(false));
        var name = user.Trim().TrimStart('@');
        if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) name = Arg("userId") == id ? Arg("userName") : "";
        context.Args[prefix] = name;
        context.Args[prefix + "Name"] = name;
        context.Args[prefix + "Id"] = id;
        context.Args[prefix + "Type"] = "kick";
        context.Args[result] = true;
    }
}

internal sealed class SendMessageSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.sendMessage";
    public override string DisplayName => "Send message to channel";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("message", "Message", ParameterKind.Multiline), UseBot, Fallback];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["kickMessageId"] = await Kick.SendChatMessageAsync(p["message"], p.GetBool("useBot"), p.GetBool("fallback", true), ct: ct)
            .ConfigureAwait(false) ?? "";
        return SubActionResult.Next;
    }
}

internal sealed class ReplyToMessageSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.replyToMessage";
    public override string DisplayName => "Reply to message";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("message", "Message", ParameterKind.Multiline),
        new("replyTo", "Message id to reply to") { DefaultValue = "%messageId%" },
        UseBot, Fallback,
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["kickMessageId"] = await Kick.SendChatMessageAsync(p["message"], p.GetBool("useBot"), p.GetBool("fallback", true),
            p["replyTo"], ct).ConfigureAwait(false) ?? "";
        return SubActionResult.Next;
    }
}

internal sealed class DeleteMessageSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.deleteMessage";
    public override string DisplayName => "Delete chat message";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("messageId", "Message id") { DefaultValue = "%messageId%" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.DeleteChatMessageAsync(p["messageId"], ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class SetTitleSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.setTitle";
    public override string DisplayName => "Set channel title (titleSuccess)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("title", "Title")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.SetTitleAsync(p["title"], ct).ConfigureAwait(false);
        context.Args["titleSuccess"] = true;
        return SubActionResult.Next;
    }
}

internal sealed class SetCategorySubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.setCategory";
    public override string DisplayName => "Set channel category (kickCategory, category.id, category.name, category.success)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("category", "Category") { Help = "Category name (searched on Kick) or numeric category id" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var category = await Kick.SetCategoryAsync(p["category"], ct).ConfigureAwait(false);
        context.Args["kickCategory"] = category.Name;
        context.Args["kickCategoryId"] = KickArgsId(category.Id);
        context.Args["category.success"] = true;
        context.Args["category.id"] = KickArgsId(category.Id);
        context.Args["category.name"] = category.Name;
        context.Args["category.thumbnail"] = category.Thumbnail;
        return SubActionResult.Next;
    }

    private static string KickArgsId(long id) => Events.KickArgs.Id(id);
}

internal sealed class BanUserSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.banUser";
    public override string DisplayName => "Ban user (bannedUser, bannedUserName, bannedUserId, banResult)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [User(), new("reason", "Reason")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.BanAsync(p["user"], p["reason"], ct).ConfigureAwait(false);
        await WriteTargetAsync(context, p["user"], "bannedUser", "banResult", ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class TimeoutUserSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.timeoutUser";
    public override string DisplayName => "Timeout user (timedOutUser, timedOutUserName, timedOutUserId, timeoutResult)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        User(),
        new("duration", "Duration (seconds)", ParameterKind.Number) { DefaultValue = "300", Help = "Kick uses whole minutes (1 minute to 7 days); rounded up" },
        new("reason", "Reason"),
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.TimeoutAsync(p["user"], p.GetInt("duration", 300), p["reason"], ct).ConfigureAwait(false);
        await WriteTargetAsync(context, p["user"], "timedOutUser", "timeoutResult", ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Kick lifts bans and timeouts with the same call; both sub-actions exist for Streamer.bot parity.</summary>
internal sealed class UnbanUserSubAction(KickService kick, string typeId, string displayName) : KickSubAction(kick)
{
    public override string TypeId => typeId;
    public override string DisplayName => displayName;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [User()];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.UnbanAsync(p["user"], ct).ConfigureAwait(false);
        // Streamer.bot names the outputs after the sub-action: unbannedUser*/unbanResult or unTimedOutUser*/unTimedOutResult.
        var (prefix, result) = typeId.Contains("imeout", StringComparison.Ordinal) ? ("unTimedOutUser", "unTimedOutResult") : ("unbannedUser", "unbanResult");
        await WriteTargetAsync(context, p["user"], prefix, result, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class AddBroadcasterInfoSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.addBroadcasterInfo";
    public override string DisplayName => "Add broadcaster information";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        foreach (var (key, value) in Kick.BroadcasterInfo()) context.Args[key] = value;
        context.Args["broadcastUserType"] = "kick";
        if (Kick.Settings.Broadcaster.UserId > 0)
        {
            try
            {
                // Streamer.bot's broadcasterProfileUrl; best effort, the rest of the information does not need it.
                var info = await Kick.GetUserInfoAsync(Events.KickArgs.Id(Kick.Settings.Broadcaster.UserId), "broadcaster", ct).ConfigureAwait(false);
                context.Args["broadcasterProfileUrl"] = info.GetValueOrDefault("broadcasterProfilePicture") ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Args["broadcasterProfileUrl"] = "";
            }
        }
        return SubActionResult.Next;
    }
}

internal sealed class GetUserInfoSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getUserInfo";
    public override string DisplayName => "Get user info for target";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [User("Target user"), new("prefix", "Argument prefix") { DefaultValue = "target" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var prefix = p["prefix"].Trim().Length > 0 ? p["prefix"].Trim() : "target";
        foreach (var (key, value) in await Kick.GetUserInfoAsync(p["user"], prefix, ct).ConfigureAwait(false)) context.Args[key] = value;
        // Streamer.bot names (with the chosen prefix, "target" by default)
        string Arg(string key) => context.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
        context.Args[prefix + "DescriptionEscaped"] = StreamerBotArgs.UrlEncode(Arg(prefix + "Description"));
        context.Args[prefix + "UserProfileImageUrl"] = Arg(prefix + "ProfilePicture");
        context.Args[prefix + "UserProfileImageUrlEscaped"] = StreamerBotArgs.UrlEncode(Arg(prefix + "ProfilePicture"));
        context.Args[prefix + "UserType"] = "kick";
        return SubActionResult.Next;
    }
}
