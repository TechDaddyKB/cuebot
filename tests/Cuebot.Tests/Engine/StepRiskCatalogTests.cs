using Cuebot.Core.Actions;

namespace Cuebot.Tests.Engine;

public sealed class StepRiskCatalogTests
{
    [Theory]
    [InlineData("twitch.sendMessage", StepRisk.Basic)]
    [InlineData("twitch.getUserInfo", StepRisk.Basic)]
    [InlineData("twitch.timeout", StepRisk.StreamControl)]
    [InlineData("twitch.ban", StepRisk.ShowCritical)]
    [InlineData("obs.setScene", StepRisk.StreamControl)]
    [InlineData("obs.streaming", StepRisk.ShowCritical)]
    [InlineData("obs.rawRequest", StepRisk.Never)]
    [InlineData("wavelink.legacy.getInputInfo", StepRisk.Basic)]
    [InlineData("wavelink.legacy.setInputVolume", StepRisk.StreamControl)]
    [InlineData("core.setGlobal", StepRisk.TrustedTech)]
    [InlineData("code.csharp", StepRisk.Never)]
    [InlineData("core.runProgram", StepRisk.Never)]
    public void Longest_rule_wins(string typeId, StepRisk expected) => Assert.Equal(expected, StepRiskCatalog.ForStep(typeId));

    [Fact]
    public void Unknown_types_are_never_available()
    {
        Assert.False(StepRiskCatalog.CoversStep("someplugin.doThing"));
        Assert.Equal(StepRisk.Never, StepRiskCatalog.ForStep("someplugin.doThing"));
        Assert.Equal(StepRisk.Never, StepRiskCatalog.ForTrigger("someplugin.event"));
    }

    [Theory]
    [InlineData("twitch.follow", StepRisk.Basic)]
    [InlineData("core.fileChanged", StepRisk.Never)]
    [InlineData("core.fileTailLine", StepRisk.Never)]
    [InlineData("desktop.clipboardChanged", StepRisk.Never)]
    [InlineData("websocket.clientMessage", StepRisk.TrustedTech)]
    public void Trigger_levels(string typeId, StepRisk expected) => Assert.Equal(expected, StepRiskCatalog.ForTrigger(typeId));
}
