using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
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
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<JsonFile<ScriptingSettings>>();
            return new CSharpScriptEngine(sp.GetRequiredService<ScriptApi>(), sp.GetRequiredService<CphServices>(),
                () => settings.Value.CSharpReferences.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray());
        });
        services.AddSingleton<CSharpPrecompiler>();

        services.AddSingleton<ISubActionHandler, CSharpCodeSubAction>();
        services.AddSingleton<ISubActionHandler>(sp =>
        {
            var cph = sp.GetRequiredService<CphServices>();
            return new CSharpMethodSubAction(sp.GetRequiredService<CSharpScriptEngine>(),
                () => cph.Profile is { } profile ? InlineCodeFinder.Names(profile.Snapshot.Actions) : []);
        });
        services.AddSingleton<ISubActionHandler, PythonCodeSubAction>();
        return services;
    }
}
