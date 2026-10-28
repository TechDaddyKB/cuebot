using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Midi.Backends;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Midi;

/// <summary>Registers MIDI in/out.</summary>
public static class MidiServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (midi.json), <see cref="MidiService"/> (an <see cref="IConnectionStatus"/>), the MIDI Message trigger,
    /// the MIDI Out sub-actions and the midi.* script functions. Call <see cref="MidiService.StartAsync"/> at startup: it
    /// opens the devices marked Auto Open and watches for unplug/replug. An <see cref="IMidiBackend"/> registered before
    /// this call replaces the platform backend (tests use a fake).
    /// </summary>
    public static IServiceCollection AddMidi(this IServiceCollection services)
    {
        services.AddSettingsFile<MidiSettings>("midi.json");
        services.AddSingleton(sp => new MidiService(sp.GetRequiredService<JsonFile<MidiSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), sp.GetService<IMidiBackend>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<MidiService>());
        services.AddSingleton<ITriggerProvider>(sp => new MidiTriggers(sp.GetRequiredService<MidiService>()));

        services.AddSingleton(sp => new SubActionSet(MidiSubActions.Create(sp.GetRequiredService<MidiService>())));
        foreach (var typeId in MidiSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));

        services.AddSingleton<IScriptFunctionProvider>(sp => new MidiScriptFunctions(sp.GetRequiredService<MidiService>()));
        return services;
    }

    /// <summary>The module's sub-actions, built once (they need the service).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
