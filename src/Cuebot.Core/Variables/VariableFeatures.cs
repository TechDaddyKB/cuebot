using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Variables;

/// <summary>Registration for arguments, global and per-user variables, and text functions.</summary>
internal static class VariableFeatures
{
    public static IServiceCollection AddVariableFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new UserVariables(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<Logging.AppLog>()));
        services.AddSingleton<Users.IViewerDataStore>(sp => sp.GetRequiredService<UserVariables>());
        services.AddSingleton(sp => sp.GetRequiredService<GlobalVariables>().Text);
        services.AddSingleton<VariableViewer>();
        services.AddSingleton<ITriggerProvider, VariableTriggers>();
        services.AddSingleton<IScriptFunctionProvider, VariableScriptFunctions>();

        services.AddSingleton<ISubActionHandler, SetArgumentSubAction>();
        services.AddSingleton<ISubActionHandler, GetGlobalSubAction>();
        services.AddSingleton<ISubActionHandler, SetGlobalSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteGlobalSubAction>();
        services.AddSingleton<ISubActionHandler, ClearTemporaryGlobalsSubAction>();
        services.AddSingleton<ISubActionHandler, GetUserVariableSubAction>();
        services.AddSingleton<ISubActionHandler, SetUserVariableSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteUserVariableSubAction>();
        services.AddSingleton<ISubActionHandler, ClearTemporaryUserVariablesSubAction>();
        services.AddSingleton<ISubActionHandler, RandomNumberSubAction>();
        services.AddSingleton<ISubActionHandler, PickColorSubAction>();
        return services;
    }
}
