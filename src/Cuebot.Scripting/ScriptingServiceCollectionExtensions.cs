using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Scripting.Compat;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Scripting;

/// <summary>Registers the scripting module: C# and Python script sub-actions and the managed Python runtime.</summary>
public static class ScriptingServiceCollectionExtensions
{
    public static IServiceCollection AddScripting(this IServiceCollection services)
    {
        services.AddSettingsFile<ScriptingSettings>("scripting.json");
        services.AddSingleton<PythonRuntime>();
        services.AddSingleton<PythonScriptHost>();

        // Streamer.bot compatibility: CPH object, CPHInline classes and custom code triggers.
        services.AddSingleton<CodeTriggerRegistry>();
        services.AddSingleton<ITriggerProvider, CodeTriggerProvider>();
        services.AddSingleton(sp => new CphServices(sp));
        services.AddSingleton(sp => new CSharpScriptEngine(sp.GetRequiredService<ScriptApi>(), sp.GetRequiredService<CphServices>()));

        services.AddSingleton<ISubActionHandler, CSharpCodeSubAction>();
        services.AddSingleton<ISubActionHandler, PythonCodeSubAction>();
        return services;
    }
}
