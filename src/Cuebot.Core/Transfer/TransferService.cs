using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Core.Transfer.StreamerBot;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Transfer;

/// <summary>
/// Import/export against the live profile, commands and globals. Preview never changes anything; apply writes the
/// chosen items. Nothing in an import is executed.
/// </summary>
public sealed class TransferService(ProfileService profile, CommandStore commands, GlobalVariables globals,
    Registry? registry = null, AppPaths? paths = null, AppLog? log = null)
{
    /// <summary>Optional user mapping additions for Streamer.bot type codes (see <see cref="StreamerBotMappingTable"/>).</summary>
    public const string MappingFileName = "streamerbot-mapping.json";

    public TransferBundle Export(ExportSelection selection) =>
        BundleExporter.Create(profile.Snapshot, commands.Snapshot, globals.Snapshot(), selection);

    public string ExportString(ExportSelection selection) => TransferCodec.Encode(Export(selection));

    public TransferTarget CurrentTarget() =>
        TransferTarget.From(profile.Snapshot, commands.Snapshot, globals.Snapshot().Select(g => g.Name));

    /// <summary>Decodes a pasted string or file content and builds the preview. Throws <see cref="TransferFormatException"/>.</summary>
    public ImportPlan Preview(string text)
    {
        var payload = TransferCodec.Decode(text);
        TransferBundle bundle;
        ImportReport report;
        if (payload.Kind == PayloadKind.Cuebot)
        {
            bundle = TransferCodec.ParseBundle(payload.Json);
            report = new ImportReport();
        }
        else
        {
            (bundle, report) = new StreamerBotImporter(MappingTable()).Convert(payload.Json);
        }
        var plan = BundleImporter.Preview(bundle, CurrentTarget(), payload.Kind, report);
        if (registry is not null) FlagUnknownTypes(plan, registry);
        return plan;
    }

    /// <summary>Writes the included items of <paramref name="plan"/> into the profile, commands and globals.</summary>
    public async Task<ImportChanges> ApplyAsync(ImportPlan plan)
    {
        var changes = BundleImporter.Resolve(plan, CurrentTarget());
        await profile.EditAsync(p =>
        {
            foreach (var queue in changes.Queues) Upsert(p.Queues, queue, q => q.Id);
            foreach (var timer in changes.Timers) Upsert(p.Timers, timer, t => t.Id);
            foreach (var action in changes.Actions) Upsert(p.Actions, action, a => a.Id);
        }).ConfigureAwait(false);
        foreach (var command in changes.Commands) commands.Save(command);
        foreach (var global in changes.Globals) globals.SetValue(global.Name, VariableValues.Normalize(global.Value), global.Persisted);
        log?.Info("Import", $"Imported '{plan.Bundle.Header.Name}': {changes.Summary}");
        return changes;
    }

    private StreamerBotMappingTable MappingTable()
    {
        var table = StreamerBotMappingTable.CreateDefault();
        var file = paths?.File(MappingFileName);
        if (file is null || !File.Exists(file)) return table;
        try
        {
            table.AddAliasesJson(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is TransferFormatException or IOException or UnauthorizedAccessException)
        {
            log?.Warn("Import", $"{MappingFileName} ignored: {ex.Message}");
        }
        return table;
    }

    private static void FlagUnknownTypes(ImportPlan plan, Registry registry)
    {
        foreach (var action in plan.Bundle.Actions)
        {
            var item = plan.Items.FirstOrDefault(i => i.Kind == TransferKind.Action && i.SourceId == action.Id);
            var missing = TransferReferences.Walk(action.SubActions).Select(s => s.Type).Where(t => registry.GetSubAction(t) is null)
                .Concat(action.Triggers.Select(t => t.Type).Where(t => registry.GetTrigger(t) is null))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var type in missing)
            {
                var message = $"'{action.Name}' uses '{type}', which this Cuebot does not have (is its module enabled?).";
                item?.Warnings.Add(message);
                if (!plan.Report.Warnings.Contains(message)) plan.Report.Warnings.Add(message);
            }
        }
    }

    private static void Upsert<T>(List<T> list, T item, Func<T, Guid> id)
    {
        var index = list.FindIndex(x => id(x) == id(item));
        if (index >= 0) list[index] = item;
        else list.Add(item);
    }
}
