using System.Globalization;
using Cuebot.Core.Scripting;

namespace Cuebot.Kick;

/// <summary>Kick functions available to user scripts (counterparts of Streamer.bot's Kick C# methods).</summary>
internal sealed class KickScriptFunctions(KickService kick) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("kick.sendMessage", "Send a message to Kick chat (useBot, fallback optional); returns the message id",
            ["message", "useBot", "fallback"], async (_, a, ct) =>
                await kick.SendChatMessageAsync(a.GetString("message"), a.GetBool("useBot"), a.GetBool("fallback", true), ct: ct).ConfigureAwait(false)),
        new("kick.reply", "Reply to a Kick chat message; returns the message id",
            ["message", "replyTo", "useBot", "fallback"], async (_, a, ct) =>
                await kick.SendChatMessageAsync(a.GetString("message"), a.GetBool("useBot"), a.GetBool("fallback", true),
                    a.GetString("replyTo"), ct).ConfigureAwait(false)),
        new("kick.deleteMessage", "Delete a Kick chat message by id", ["messageId"], async (_, a, ct) =>
        {
            await kick.DeleteChatMessageAsync(a.GetString("messageId"), ct).ConfigureAwait(false);
            return null;
        }),
        new("kick.setTitle", "Set the Kick stream title", ["title"], async (_, a, ct) =>
        {
            await kick.SetTitleAsync(a.GetString("title"), ct).ConfigureAwait(false);
            return null;
        }),
        new("kick.setCategory", "Set the Kick category by name or id; returns the category name", ["category"], async (_, a, ct) =>
            (await kick.SetCategoryAsync(a.GetString("category"), ct).ConfigureAwait(false)).Name),
        new("kick.ban", "Ban a Kick user (name or id)", ["user", "reason"], async (_, a, ct) =>
        {
            await kick.BanAsync(a.GetString("user"), a.GetString("reason"), ct).ConfigureAwait(false);
            return null;
        }),
        new("kick.timeout", "Time out a Kick user for some seconds (rounded up to minutes)", ["user", "seconds", "reason"], async (_, a, ct) =>
        {
            var seconds = int.TryParse(a.GetString("seconds", "300"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 300;
            await kick.TimeoutAsync(a.GetString("user"), seconds, a.GetString("reason"), ct).ConfigureAwait(false);
            return null;
        }),
        new("kick.unban", "Lift a ban or timeout on Kick", ["user"], async (_, a, ct) =>
        {
            await kick.UnbanAsync(a.GetString("user"), ct).ConfigureAwait(false);
            return null;
        }),
        new("kick.getBroadcaster", "Broadcaster and bot account details", [], (_, _, _) =>
            Task.FromResult<object?>(kick.BroadcasterInfo())),
        new("kick.getUserInfo", "Profile and channel of a Kick user (name or id)", ["user"], async (_, a, ct) =>
            await kick.GetUserInfoAsync(a.GetString("user"), "", ct).ConfigureAwait(false)),
    ];
}
