using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Core.Webhooks;
using Cuebot.Kick.SubActions;
using Cuebot.Kick.Triggers;
using Cuebot.Kick.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Kick;

/// <summary>Registers the Kick module.</summary>
public static class KickServiceCollectionExtensions
{
    /// <summary>
    /// Adds Kick settings (kick.json), <see cref="KickService"/>, its combined-chat adapter and sender, the Kick
    /// channel for the shared emote service, triggers, sub-actions, script functions and the "kick" webhook handler
    /// (<see cref="KickWebhookHandler"/>) for the webhook relay.
    /// </summary>
    public static IServiceCollection AddKick(this IServiceCollection services)
    {
        services.AddSettingsFile<KickSettings>("kick.json");
        services.AddSingleton(sp =>
        {
            var kick = new KickService(
                sp.GetRequiredService<JsonFile<KickSettings>>(),
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<EventBus>(),
                sp.GetRequiredService<AppLog>());
            // Resolved when needed: the emote service itself depends on the Kick channel source below.
            kick.EmoteRefresh = ct => sp.GetService<EmoteService>()?.RefreshAsync(force: true, ct) ?? Task.CompletedTask;
            return kick;
        });
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<KickService>());
        services.AddSingleton<IEmoteChannelSource>(sp => new KickEmoteChannelSource(sp.GetRequiredService<KickService>()));
        services.AddSingleton<IChatEventAdapter, KickChatAdapter>();
        services.AddSingleton<IPresentViewersArgs>(sp => new KickPresentViewersArgs(sp.GetRequiredService<KickService>()));
        services.AddSingleton<Cuebot.Core.Chat.Moderation.IChatModerator>(sp => new KickChatModerator(sp.GetRequiredService<KickService>()));
        services.AddSingleton<IChatSender, KickChatSender>();
        services.AddSingleton<ITriggerProvider, KickTriggers>();
        services.AddSingleton<ISubActionHandler, SendMessageSubAction>();
        services.AddSingleton<ISubActionHandler, ReplyToMessageSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteMessageSubAction>();
        services.AddSingleton<ISubActionHandler, SetTitleSubAction>();
        services.AddSingleton<ISubActionHandler, SetCategorySubAction>();
        services.AddSingleton<ISubActionHandler, BanUserSubAction>();
        services.AddSingleton<ISubActionHandler, TimeoutUserSubAction>();
        services.AddSingleton<ISubActionHandler>(sp => new UnbanUserSubAction(sp.GetRequiredService<KickService>(), "kick.unbanUser", "Unban user"));
        services.AddSingleton<ISubActionHandler>(sp => new UnbanUserSubAction(sp.GetRequiredService<KickService>(), "kick.untimeoutUser", "Remove timeout"));
        services.AddSingleton<ISubActionHandler, AddBroadcasterInfoSubAction>();
        services.AddSingleton<ISubActionHandler, GetUserInfoSubAction>();
        services.AddSingleton<ISubActionHandler, KickAddRandomUsersSubAction>();
        services.AddSingleton<ISubActionHandler, KickAddUserToGroupSubAction>();
        services.AddSingleton<ISubActionHandler, KickRemoveUserFromGroupSubAction>();
        services.AddSingleton<ISubActionHandler, KickUserInGroupSubAction>();
        services.AddSingleton<ISubActionHandler, CreateRewardSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteRewardSubAction>();
        services.AddSingleton<ISubActionHandler, GetRewardInfoSubAction>();
        services.AddSingleton<ISubActionHandler, UpdateRedemptionSubAction>();
        services.AddSingleton<ISubActionHandler, UpdatePendingRedemptionsSubAction>();
        services.AddSingleton<ISubActionHandler, GetRewardCounterSubAction>();
        services.AddSingleton<ISubActionHandler>(sp => new ResetRewardCounterSubAction(sp.GetRequiredService<KickService>(), users: false));
        services.AddSingleton<ISubActionHandler>(sp => new ResetRewardCounterSubAction(sp.GetRequiredService<KickService>(), users: true));
        services.AddSingleton<ISubActionHandler>(sp => new SetRewardGroupStateSubAction(sp.GetRequiredService<KickService>(), paused: false));
        services.AddSingleton<ISubActionHandler>(sp => new SetRewardGroupStateSubAction(sp.GetRequiredService<KickService>(), paused: true));
        services.AddSingleton<ISubActionHandler, ConfigureRewardsSubAction>();
        foreach (var typeId in RewardFieldSubActions)
            services.AddSingleton<ISubActionHandler>(sp =>
                UpdateRewardSubAction.All(sp.GetRequiredService<KickService>()).Single(s => s.TypeId == typeId));
        services.AddSingleton<IScriptFunctionProvider, KickScriptFunctions>();
        // Kick webhooks arrive through the webhook relay (Cuebot.Relay), which dispatches route "kick" here.
        services.AddSingleton<IWebhookHandler>(sp =>
            new KickWebhookHandler(sp.GetRequiredService<KickService>().Webhooks, sp.GetRequiredService<EventBus>()));
        return services;
    }

    private static readonly string[] RewardFieldSubActions =
    [
        "kick.setRewardEnabled", "kick.setRewardPaused", "kick.setRewardTitle", "kick.setRewardCost", "kick.setRewardDescription",
        "kick.setRewardColor", "kick.setRewardUserInput", "kick.setRewardSkipQueue",
    ];
}

/// <summary>Tells the shared emote service which Kick channel (broadcaster user id) to load 7TV emotes for.</summary>
internal sealed class KickEmoteChannelSource(KickService kick) : IEmoteChannelSource
{
    public string Platform => KickChatAdapter.PlatformName;

    public string? ChannelId => kick.Settings.Broadcaster.UserId > 0 ? Events.KickArgs.Id(kick.Settings.Broadcaster.UserId) : null;
}
