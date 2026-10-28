using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Transfer;

namespace Cuebot.UiTests.Transfer;

/// <summary>
/// Imports every real Streamer.bot export string in CUEBOT_SB_SAMPLES (else C:\Users\docker\cuebot-sb-exports\samples)
/// the way the Import page does, opens each imported action and step in the editor, restarts and imports them all
/// again over the top. Does nothing without samples; they are third-party content and are never committed.
/// Nothing imported is run.
/// </summary>
public sealed class StreamerBotSampleImportTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-sbsamples-").FullName;

    private static IReadOnlyList<(string Name, string Text)> Samples()
    {
        var folder = Environment.GetEnvironmentVariable("CUEBOT_SB_SAMPLES") is { Length: > 0 } env ? env : @"C:\Users\docker\cuebot-sb-exports\samples";
        if (!Directory.Exists(folder)) return [];
        var samples = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.sb").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            try
            {
                TransferCodec.Decode(text);
                samples.Add((Path.GetFileName(file), text));
            }
            catch (TransferFormatException)
            {
                // an incomplete download; the Cuebot.Tests samples test reports it
            }
        }
        return samples;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Every_sample_imports_opens_in_the_editor_and_survives_a_restart()
    {
        var samples = Samples();
        if (samples.Count == 0) return;
        var problems = new List<string>();

        Dictionary<Guid, string> before;
        int commands;
        await using (var host = AppHost.Create(new AppPaths(_dataDir)))
        {
            var transfer = host.Get<TransferService>();
            foreach (var (name, text) in samples)
            {
                var plan = transfer.Preview(text);
                // The full app has every module, so a missing type means a mapping targets something that does not exist.
                problems.AddRange(plan.Report.Warnings.Where(w => w.Contains("which this Cuebot does not have")).Select(w => $"{name}: {w}"));
                var changes = await transfer.ApplyAsync(plan);
                output.WriteLine($"{name}: {changes.Summary}");
            }

            TestApp.BindingErrors.Clear();
            var vm = new MainWindowViewModel(host);
            var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
            window.Show();
            var actions = vm.Pages.OfType<ActionsViewModel>().Single();
            vm.SelectedPage = actions;
            Pump();
            var opened = 0;
            foreach (var item in actions.Actions.ToList())
            {
                actions.SelectedAction = item;
                Pump();
                foreach (var trigger in item.Triggers)
                {
                    item.Selected = trigger;
                    Pump();
                }
                foreach (var step in Steps(item.Steps.Items))
                {
                    item.Selected = step;
                    Pump();
                    opened++;
                }
            }
            output.WriteLine($"Opened {actions.Actions.Count} actions, {opened} steps");
            problems.AddRange(TestApp.BindingErrors.Errors.Distinct().Select(e => $"binding: {e}"));
            window.Close();

            before = host.Get<ProfileService>().Snapshot.Actions.ToDictionary(a => a.Id, Describe);
            commands = host.Get<CommandStore>().Snapshot.Count;
        }

        await using (var host = AppHost.Create(new AppPaths(_dataDir)))
        {
            var after = host.Get<ProfileService>().Snapshot.Actions.ToDictionary(a => a.Id, Describe);
            foreach (var (id, text) in before)
                if (!after.TryGetValue(id, out var reloaded) || reloaded != text) problems.Add($"changed by the restart: {text.Split('\n')[0]}");
            Assert.Equal(commands, host.Get<CommandStore>().Snapshot.Count);

            // The same strings again: everything now exists, and importing over it must still work.
            var transfer = host.Get<TransferService>();
            foreach (var (_, text) in samples) await transfer.ApplyAsync(transfer.Preview(text));
            Assert.True(host.Get<ProfileService>().Snapshot.Actions.Count >= before.Count);
        }

        foreach (var problem in problems) output.WriteLine(problem);
        Assert.Empty(problems);
    }

    private static IEnumerable<SubActionItemViewModel> Steps(IEnumerable<SubActionItemViewModel> items)
    {
        foreach (var step in items)
        {
            yield return step;
            foreach (var nested in Steps(step.Branches.SelectMany(b => b.Steps.Items))) yield return nested;
        }
    }

    // Name, triggers and every step's type and parameters, in order.
    private static string Describe(ActionDefinition action) =>
        string.Join("\n", new[] { $"{action.Name} enabled={action.Enabled}" }
            .Concat(action.Triggers.Select(t => $"T {t.Type} {string.Join(";", t.Settings.OrderBy(k => k.Key, StringComparer.Ordinal))}"))
            .Concat(TransferReferences.Walk(action.SubActions).Select(s => $"S {s.Type} {s.Enabled} {string.Join(";", s.Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))}")));

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
