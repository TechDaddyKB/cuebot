using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Points;
using Cuebot.Core.Profiles;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Tests.Profiles;

public sealed class ProfileSwitcherTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly ServiceProvider _services;
    private readonly ProfileCatalog _catalog;
    private readonly ProfileSwitcher _switcher;
    private readonly TaskCompletionSource<string> _switchedTo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProfileSwitcherTests()
    {
        _catalog = ProfileCatalog.Open(_dir.Path);
        _catalog.Rename("main", "Kitsune");
        _catalog.Create("Night Owl");
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_catalog.ActiveFolder, _dir.Path)).BuildServiceProvider();
        _services.GetRequiredService<ActionEngine>().Start();
        _switcher = _services.GetRequiredService<ProfileSwitcher>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        SecretStore.ResetCache(_dir.Path);
        _dir.Dispose();
    }

    private void Attach(ProfileEntry? previous = null) =>
        _switcher.Attach(_catalog, _catalog.Find("main")!, previous, id => _switchedTo.TrySetResult(id));

    private void AddAction(string trigger, params SubActionDefinition[] steps)
    {
        var profile = _services.GetRequiredService<ProfileService>();
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = trigger, Triggers = [new TriggerDefinition { Type = trigger }], SubActions = [.. steps],
        });
        profile.NotifyChanged();
    }

    private static SubActionDefinition SetGlobal(string name, string value) =>
        new() { Type = "core.setGlobal", Parameters = { ["name"] = name, ["value"] = value, ["persisted"] = "false" } };

    [Fact]
    public void Without_a_shell_there_are_no_profiles()
    {
        Assert.Equal(ProfileSwitchResult.NotAvailable, _switcher.Switch("night-owl"));
        Assert.Null(_switcher.Current);
        Assert.Empty(_switcher.Profiles);
    }

    [Fact]
    public async Task Switching_runs_the_stopping_actions_first_and_then_hands_over()
    {
        Attach();
        AddAction("core.profileStopping",
            new SubActionDefinition { Type = "core.delay", Parameters = { ["milliseconds"] = "300" } },
            SetGlobal("goodbye", "%nextProfile%"));

        Assert.Equal(ProfileSwitchResult.Switching, _switcher.Switch("Night Owl"));
        Assert.True(_switcher.IsSwitching);
        Assert.Equal(ProfileSwitchResult.Busy, _switcher.Switch("night-owl"));

        Assert.Equal("night-owl", await _switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Night Owl", _services.GetRequiredService<GlobalVariables>().Get("goodbye"));
    }

    [Fact]
    public async Task Unknown_open_and_live_are_refused()
    {
        Attach();
        Assert.Equal(ProfileSwitchResult.NotFound, _switcher.Switch("nobody"));
        Assert.Equal(ProfileSwitchResult.AlreadyOpen, _switcher.Switch("Kitsune"));

        _services.GetRequiredService<PointsService>().SetLive("Twitch", true);
        Assert.Equal(["Twitch"], _switcher.LivePlatforms);
        Assert.Equal(ProfileSwitchResult.Live, _switcher.Switch("night-owl"));
        Assert.False(_switcher.IsSwitching);
        Assert.Equal(ProfileSwitchResult.Switching, _switcher.Switch("night-owl", evenWhileLive: true));
        Assert.Equal("night-owl", await _switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Profile_started_names_the_profile_and_the_one_before()
    {
        Attach(previous: _catalog.Find("night-owl"));
        AddAction("core.profileStarted", SetGlobal("started", "%profile% after %previousProfile% (%previousProfileId%)"));

        _switcher.PublishStarted();

        var globals = _services.GetRequiredService<GlobalVariables>();
        for (var i = 0; i < 100 && globals.Get("started") is null; i++) await Task.Delay(50);
        Assert.Equal("Kitsune after Night Owl (night-owl)", globals.Get("started"));
    }

    [Fact]
    public async Task The_switch_profile_step_switches_and_fails_while_live()
    {
        Attach();
        var engine = _services.GetRequiredService<ActionEngine>();
        var step = new SubActionDefinition { Type = "core.switchProfile", Parameters = { ["profile"] = "Night Owl" } };

        _services.GetRequiredService<PointsService>().SetLive("YouTube", true);
        Assert.False(await engine.RunAsync(new ActionDefinition { Name = "while live", SubActions = [step] }));
        Assert.False(_switcher.IsSwitching);

        step.Parameters["evenWhileLive"] = "true";
        Assert.True(await engine.RunAsync(new ActionDefinition { Name = "anyway", SubActions = [step] }));
        Assert.Equal("night-owl", await _switchedTo.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_new_triggers_and_step_are_registered_and_kept_from_mods()
    {
        var registry = _services.GetRequiredService<Registry>();
        Assert.NotNull(registry.GetTrigger(CoreEvents.ProfileStarted));
        Assert.NotNull(registry.GetTrigger(CoreEvents.ProfileStopping));
        Assert.NotNull(registry.GetSubAction("core.switchProfile"));
        Assert.Equal(StepRisk.Never, StepRiskCatalog.ForStep("core.switchProfile"));
    }
}
