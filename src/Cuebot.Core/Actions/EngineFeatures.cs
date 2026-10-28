using Cuebot.Core.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Actions;

/// <summary>Registration for the action engine: flow and logic sub-actions, queues, history, signals.</summary>
internal static class EngineFeatures
{
    public static IServiceCollection AddEngineFeatures(this IServiceCollection services)
    {
        // Services ActionEngine picks up through its optional constructor parameters.
        services.AddSingleton<QueueService>();
        services.AddSingleton(_ => new ActionHistory());
        services.AddSingleton<IRandomSource>(SystemRandomSource.Instance);
        services.AddSingleton<SignalService>();

        services.AddSingleton<ISubActionHandler, DelaySubAction>();
        services.AddSingleton<ISubActionHandler, LogSubAction>();
        services.AddSingleton<ISubActionHandler, CommentSubAction>();
        services.AddSingleton<ISubActionHandler, GroupSubAction>();
        services.AddSingleton<ISubActionHandler, IfSubAction>();
        services.AddSingleton<ISubActionHandler, SwitchSubAction>();
        services.AddSingleton<ISubActionHandler, WhileSubAction>();
        services.AddSingleton<ISubActionHandler, LoopBreakSubAction>();
        services.AddSingleton<ISubActionHandler, LoopContinueSubAction>();
        services.AddSingleton<ISubActionHandler, GetInputSubAction>();
        services.AddSingleton<ISubActionHandler, StopSubAction>();
        services.AddSingleton<ISubActionHandler, RunActionSubAction>();
        services.AddSingleton<ISubActionHandler, SendSignalSubAction>();
        services.AddSingleton<ISubActionHandler, WaitForSignalSubAction>();
        services.AddSingleton<ISubActionHandler, SetActionStateSubAction>();
        services.AddSingleton<ISubActionHandler, GetActionStateSubAction>();
        services.AddSingleton<UserPromptService>();
        services.AddSingleton<ISubActionHandler, GetInputDialogSubAction>();
        services.AddSingleton<ISubActionHandler, ClearActionQueueSubAction>();
        services.AddSingleton<ISubActionHandler, SetActionQueueBlockingSubAction>();
        services.AddSingleton<ISubActionHandler, SetActionQueuePauseSubAction>();
        services.AddSingleton<ISubActionHandler, GetActionGroupStateSubAction>();
        services.AddSingleton<ISubActionHandler, SetActionGroupStateSubAction>();
        services.AddSingleton<ISubActionHandler, TriggerCustomEventSubAction>();
        // These name triggers and steps by their display names, so they read the registry lazily (it contains them).
        services.AddSingleton<ISubActionHandler>(sp => new SetTriggerStateSubAction(sp.GetRequiredService<ProfileService>(), LazyRegistry(sp)));
        services.AddSingleton<ISubActionHandler>(sp => new SetSubActionStateSubAction(sp.GetRequiredService<ProfileService>(), LazyRegistry(sp)));
        services.AddSingleton<ISubActionHandler>(sp => new SetSubActionWeightSubAction(sp.GetRequiredService<ProfileService>(), LazyRegistry(sp)));
        services.AddSingleton<ITriggerProvider, CoreTriggers>();
        return services;
    }

    private static Lazy<Registry> LazyRegistry(IServiceProvider sp) => new(sp.GetRequiredService<Registry>);
}
