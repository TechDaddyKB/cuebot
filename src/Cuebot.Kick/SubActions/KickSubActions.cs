using Cuebot.Core.Actions;
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
    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct);

    protected static ParameterDescriptor UseBot { get; } =
        new("useBot", "Send as bot account", ParameterKind.Boolean) { DefaultValue = "false" };

    protected static ParameterDescriptor Fallback { get; } =
        new("fallback", "Use the broadcaster when the bot is not signed in", ParameterKind.Boolean) { DefaultValue = "true" };

    protected static ParameterDescriptor User(string label = "User") =>
        new("user", label) { DefaultValue = "%userName%", Help = "Kick user name or numeric user id" };
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
    public override string DisplayName => "Set channel title";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("title", "Title")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.SetTitleAsync(p["title"], ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class SetCategorySubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.setCategory";
    public override string DisplayName => "Set channel category";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("category", "Category") { Help = "Category name (searched on Kick) or numeric category id" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var category = await Kick.SetCategoryAsync(p["category"], ct).ConfigureAwait(false);
        context.Args["kickCategory"] = category.Name;
        context.Args["kickCategoryId"] = KickArgsId(category.Id);
        return SubActionResult.Next;
    }

    private static string KickArgsId(long id) => Events.KickArgs.Id(id);
}

internal sealed class BanUserSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.banUser";
    public override string DisplayName => "Ban user";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [User(), new("reason", "Reason")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.BanAsync(p["user"], p["reason"], ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class TimeoutUserSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.timeoutUser";
    public override string DisplayName => "Timeout user";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        User(),
        new("duration", "Duration (seconds)", ParameterKind.Number) { DefaultValue = "300", Help = "Kick uses whole minutes (1 minute to 7 days); rounded up" },
        new("reason", "Reason"),
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.TimeoutAsync(p["user"], p.GetInt("duration", 300), p["reason"], ct).ConfigureAwait(false);
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
        return SubActionResult.Next;
    }
}

internal sealed class AddBroadcasterInfoSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.addBroadcasterInfo";
    public override string DisplayName => "Add broadcaster information";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        foreach (var (key, value) in Kick.BroadcasterInfo()) context.Args[key] = value;
        return Task.FromResult(SubActionResult.Next);
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
        return SubActionResult.Next;
    }
}
