using Cuebot.Core;
using Cuebot.Core.Actions;
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
        services.AddSingleton<CSharpScriptEngine>();
        services.AddSingleton<ISubActionHandler, CSharpCodeSubAction>();
        services.AddSingleton<ISubActionHandler, PythonCodeSubAction>();
        return services;
    }
}
