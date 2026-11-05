using Cuebot.Core.Actions;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

/// <summary>
/// Steps a mod portal member added or changed never feed steps that run code, programs or files. The Record step
/// stands in for one: like any type <see cref="StepRiskCatalog"/> does not list, it counts as Never.
/// </summary>
public sealed class ModInfluenceTests : IDisposable
{
    private static readonly Guid Mod = Guid.NewGuid();
    private readonly EngineHarness _h = new();

    private static SubActionDefinition ByMod(SubActionDefinition step)
    {
        step.EditedBy = Mod;
        return step;
    }

    [Fact]
    public void Record_stands_in_for_a_never_step() => Assert.Equal(StepRisk.Never, StepRiskCatalog.ForStep(RecordSubAction.Id));

    [Fact]
    public async Task Streamer_steps_run_as_before()
    {
        var action = _h.Add("own", SetArg("x", "1"), Record("ran"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["ran"], _h.Calls);
    }

    [Fact]
    public async Task A_mod_step_blocks_later_never_steps_in_the_run()
    {
        var action = _h.Add("edited", ByMod(SetArg("x", "1")), SetArg("y", "2"), Record("must not run"));
        Assert.False(await _h.Run(action));
        Assert.Empty(_h.Calls);
        var entry = _h.Engine.History.Entries[0];
        Assert.Contains("a mod changed an earlier step", entry.Error);
        Assert.Equal("2", entry.ArgsAfterRun!["y"]?.ToString());
        Assert.True(ModInfluence.IsIn(entry.ArgsAfterRun!));
    }

    [Fact]
    public async Task A_mod_cannot_clear_the_mark_with_set_argument()
    {
        var action = _h.Add("sneaky", ByMod(SetArg(ModInfluence.ArgName, "false")), Record("must not run"));
        Assert.False(await _h.Run(action));
        Assert.Empty(_h.Calls);
    }

    [Fact]
    public async Task The_mark_follows_into_actions_a_mod_step_runs()
    {
        _h.Add("streamer's", Record("program"));
        var caller = _h.Add("caller", ByMod(Step("core.runAction", ("action", "streamer's"), ("wait", "true"))));
        await _h.Run(caller);
        Assert.Empty(_h.Calls);

        var plain = _h.Add("plain caller", Step("core.runAction", ("action", "streamer's"), ("wait", "true")));
        Assert.True(await _h.Run(plain));
        Assert.Equal(["program"], _h.Calls);
    }

    [Fact]
    public async Task Variables_a_mod_set_are_refused_by_never_steps_until_the_streamer_sets_them()
    {
        var modSets = _h.Add("mod sets", ByMod(Step("core.setGlobal", ("name", "target"), ("value", "C:\\evil.exe"), ("persisted", "false"))));
        Assert.True(await _h.Run(modSets));
        Assert.Contains("target", _h.Globals.SetByMod());

        var uses = _h.Add("uses it", Record("~target~"));
        Assert.False(await _h.Run(uses));
        Assert.Contains("'target', which a mod set", _h.Engine.History.Entries[0].Error);
        Assert.Empty(_h.Calls);

        var streamerSets = _h.Add("streamer sets", Step("core.setGlobal", ("name", "target"), ("value", "notepad"), ("persisted", "false")));
        Assert.True(await _h.Run(streamerSets));
        Assert.Empty(_h.Globals.SetByMod());
        Assert.True(await _h.Run(uses));
        Assert.Equal(["notepad"], _h.Calls);
    }

    public void Dispose() => _h.Dispose();
}
