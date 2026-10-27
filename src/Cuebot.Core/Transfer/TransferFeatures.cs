using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Transfer;

/// <summary>Registers import/export (Cuebot bundles and Streamer.bot import strings).</summary>
public static class TransferFeatures
{
    public static IServiceCollection AddTransferFeatures(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(TransferService))) return services; // idempotent
        services.AddSingleton(sp => new TransferService(
            sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<GlobalVariables>(),
            sp.GetService<Registry>(), sp.GetService<AppPaths>(), sp.GetService<AppLog>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new TransferScriptFunctions(sp.GetRequiredService<TransferService>(),
            sp.GetRequiredService<ProfileService>(), sp.GetRequiredService<CommandStore>()));
        return services;
    }
}

/// <summary><c>transfer.export</c>: builds a Cuebot import string from actions, commands and timers named by the script.</summary>
public sealed class TransferScriptFunctions(TransferService transfer, ProfileService profile, CommandStore commands) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("transfer.export",
            "Build a Cuebot import string (CUEBOT1:…). Names are comma separated; dependencies are included unless includeDependencies is false",
            ["actions", "commands", "timers", "includeDependencies", "name", "author", "description"],
            (_, a, _) => Task.FromResult<object?>(Export(a))),
    ];

    private string Export(JsonElement a)
    {
        var snapshot = profile.Snapshot;
        var selection = new ExportSelection
        {
            IncludeDependencies = a.GetBool("includeDependencies", true),
            Header = new TransferHeader { Name = a.GetString("name"), Author = a.GetString("author"), Description = a.GetString("description") },
        };
        foreach (var name in Names(a, "actions"))
            selection.ActionIds.Add((snapshot.FindAction(name) ?? throw new ScriptException($"No action named '{name}'")).Id);
        foreach (var name in Names(a, "commands"))
            selection.CommandIds.Add((BundleExporter.FindCommand(commands.Snapshot, name) ?? throw new ScriptException($"No command named '{name}'")).Id);
        foreach (var name in Names(a, "timers"))
            selection.TimerIds.Add((BundleExporter.FindTimer(snapshot.Timers, name) ?? throw new ScriptException($"No timer named '{name}'")).Id);
        return transfer.ExportString(selection);
    }

    private static IEnumerable<string> Names(JsonElement a, string key) =>
        a.GetString(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
