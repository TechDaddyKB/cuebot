using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;

namespace Cuebot.UiTests;

/// <summary>
/// Every trigger and sub-action the real composition root loads has a mod portal risk level
/// (<see cref="StepRiskCatalog"/>), so a new type never falls back to "Never" by accident, or worse, inherits a
/// level from a prefix it should not.
/// </summary>
public sealed class StepRiskCoverageTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-risk-").FullName;
    private readonly AppHost _host;

    public StepRiskCoverageTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private Registry Registry => _host.Get<Registry>();

    [Fact]
    public void Every_registered_step_and_trigger_type_has_a_risk_level()
    {
        var missing = Registry.SubActions.Where(h => !StepRiskCatalog.CoversStep(h.TypeId)).Select(h => "step " + h.TypeId)
            .Concat(Registry.Triggers.Where(t => !StepRiskCatalog.CoversTrigger(t.TypeId)).Select(t => "trigger " + t.TypeId))
            .ToList();
        Assert.True(missing.Count == 0, "Add these to StepRiskCatalog:\n" + string.Join("\n", missing));
    }

    [Fact]
    public async Task The_portal_resolves_alongside_every_connection()
    {
        // The portal's controls list the connections, and the portal relay is one of them: this must not loop.
        var ct = TestContext.Current.CancellationToken;
        var resolve = Task.Run(() => _host.GetAll<IConnectionStatus>().Count(), ct);
        Assert.True(await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(30), ct)) == resolve,
            "Resolving the connections hung (a dependency loop).");
        Assert.NotNull(_host.Get<Cuebot.Portal.PortalControls>());
    }

    [Fact]
    public void Code_programs_and_files_are_never_available()
    {
        foreach (var h in Registry.SubActions)
        {
            var codeParameter = h.Parameters.Any(p => p.Kind == ParameterKind.Code);
            if (codeParameter || h.TypeId.StartsWith("code.", StringComparison.Ordinal))
                Assert.True(StepRiskCatalog.ForStep(h.TypeId) == StepRisk.Never, $"{h.TypeId} runs code but is not Never.");
        }
        foreach (var id in new[] { "core.runProgram", "core.writeFile", "core.readFile", "core.deleteFile", "input.keyboardPress", "obs.rawRequest" })
            Assert.Equal(StepRisk.Never, StepRiskCatalog.ForStep(id));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
