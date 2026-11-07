using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;

namespace Cuebot.Core.Profiles;

/// <summary>What asking to switch profile did.</summary>
public enum ProfileSwitchResult
{
    /// <summary>The switch started: "Profile stopping" actions run, then Cuebot opens again on the other profile.</summary>
    Switching,

    /// <summary>That profile is the open one.</summary>
    AlreadyOpen,

    /// <summary>There is no profile with that name or id.</summary>
    NotFound,

    /// <summary>A stream is live and the caller did not say to switch anyway.</summary>
    Live,

    /// <summary>A switch is already under way.</summary>
    Busy,

    /// <summary>This Cuebot runs without profiles (tests, tools).</summary>
    NotAvailable,
}

/// <summary>
/// Switches streamer profiles for everything that can ask (the top bar, the tray, the jump box, Settings, the
/// "Switch profile" step, <c>--profile</c>, the REST API). It refuses while a stream is live unless told otherwise, runs
/// the "Profile stopping" actions and waits for them (at most <see cref="StoppingTimeout"/>), then hands over to the
/// shell, which restarts Cuebot on the other profile. Without a shell attached (tests, tools) there are no profiles.
/// </summary>
public sealed class ProfileSwitcher(IServiceProvider services, PointsService points, EventBus bus, AppLog log)
{
    // The engine is found when a switch starts: the engine's registry holds the "Switch profile" step, which needs this.
    private ActionEngine Engine => (ActionEngine)services.GetService(typeof(ActionEngine))!;

    public static readonly TimeSpan StoppingTimeout = TimeSpan.FromSeconds(10);

    private ProfileCatalog? _catalog;
    private Action<string>? _switchNow;
    private int _switching;

    /// <summary>The profile this Cuebot opened, or null without profiles.</summary>
    public ProfileEntry? Current { get; private set; }

    /// <summary>The profile Cuebot was on before a switch opened this one, or null.</summary>
    public ProfileEntry? Previous { get; private set; }

    public IReadOnlyList<ProfileEntry> Profiles => _catalog?.Profiles ?? [];

    /// <summary>Platforms that are streaming right now (from stream online and offline events).</summary>
    public IReadOnlyList<string> LivePlatforms => points.LivePlatforms;

    /// <summary>A switch is under way (its "Profile stopping" actions are running).</summary>
    public bool IsSwitching => Volatile.Read(ref _switching) != 0;

    /// <summary>
    /// Called by the shell once: the profiles, the open and previous profile, and how to restart on another profile
    /// (it makes it the active one and quits; the process then starts again).
    /// </summary>
    public void Attach(ProfileCatalog catalog, ProfileEntry current, ProfileEntry? previous, Action<string> switchNow)
    {
        _catalog = catalog;
        Current = current;
        Previous = previous;
        _switchNow = switchNow;
    }

    /// <summary>A profile by id or name; null when there is none.</summary>
    public ProfileEntry? Find(string idOrName) => _catalog?.Find(idOrName);

    /// <summary>
    /// Starts switching to <paramref name="idOrName"/>. While a stream is live this returns <see cref="ProfileSwitchResult.Live"/>
    /// and does nothing, unless <paramref name="evenWhileLive"/>.
    /// </summary>
    public ProfileSwitchResult Switch(string idOrName, bool evenWhileLive = false)
    {
        if (_catalog is null || _switchNow is null || Current is null) return ProfileSwitchResult.NotAvailable;
        if (_catalog.Find(idOrName) is not { } target) return ProfileSwitchResult.NotFound;
        if (target.Id == Current.Id) return ProfileSwitchResult.AlreadyOpen;
        if (!evenWhileLive && LivePlatforms.Count > 0) return ProfileSwitchResult.Live;
        if (Interlocked.Exchange(ref _switching, 1) != 0) return ProfileSwitchResult.Busy;
        log.Info("Profiles", $"Switching from {Current.Name} to {target.Name}");
        _ = Task.Run(() => SwitchAsync(target));
        return ProfileSwitchResult.Switching;
    }

    private async Task SwitchAsync(ProfileEntry target)
    {
        try
        {
            var runs = Engine.Dispatch(new BotEvent(CoreEvents.ProfileStopping, new Dictionary<string, object?>
            {
                ["profile"] = Current!.Name, ["profileId"] = Current.Id, ["nextProfile"] = target.Name, ["nextProfileId"] = target.Id,
            }));
            if (runs.Count > 0)
            {
                var all = Task.WhenAll(runs);
                if (await Task.WhenAny(all, Task.Delay(StoppingTimeout)).ConfigureAwait(false) != all)
                    log.Warn("Profiles", $"'Profile stopping' actions were still running after {StoppingTimeout.TotalSeconds:0} s; switching anyway");
            }
        }
        catch (Exception ex)
        {
            log.Error("Profiles", "A 'Profile stopping' action failed; switching anyway", ex);
        }
        _switchNow!(target.Id);
    }

    /// <summary>Raises "Profile started" for the open profile (once Cuebot has finished starting).</summary>
    public void PublishStarted()
    {
        if (Current is null) return;
        bus.Publish(new BotEvent(CoreEvents.ProfileStarted, new Dictionary<string, object?>
        {
            ["profile"] = Current.Name, ["profileId"] = Current.Id,
            ["previousProfile"] = Previous?.Name ?? "", ["previousProfileId"] = Previous?.Id ?? "",
        }));
    }
}

/// <summary>
/// "Switch profile": opens Cuebot on another persona. Put it last in its action: Cuebot closes and opens again on
/// the other profile once the "Profile stopping" actions are done.
/// </summary>
public sealed class SwitchProfileSubAction(ProfileSwitcher switcher) : ISubActionHandler
{
    public string TypeId => "core.switchProfile";
    public string Category => Categories.Core;
    public string DisplayName => "Switch profile";

    public IReadOnlyList<ParameterDescriptor> Parameters => _parameters ??=
    [
        new("profile", "Profile") { Suggestions = () => switcher.Profiles.Select(p => p.Name), Help = "The profile's name, like Night Owl" },
        new("evenWhileLive", "Switch even while live", ParameterKind.Boolean) { DefaultValue = "false" },
    ];
    private IReadOnlyList<ParameterDescriptor>? _parameters;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var name = p["profile"].Trim();
        var result = switcher.Switch(name, p.GetBool("evenWhileLive"));
        context.Args["profileSwitch"] = result.ToString();
        return result switch
        {
            ProfileSwitchResult.Switching or ProfileSwitchResult.AlreadyOpen => Task.FromResult(SubActionResult.Next),
            ProfileSwitchResult.NotFound => throw new ArgumentException($"There is no profile '{name}'."),
            ProfileSwitchResult.Live => throw new InvalidOperationException(
                $"Not switching to '{name}': you are live on {string.Join(", ", switcher.LivePlatforms)}. Tick 'Switch even while live' to switch anyway."),
            ProfileSwitchResult.Busy => throw new InvalidOperationException("A switch to another profile is already under way."),
            _ => throw new InvalidOperationException("This Cuebot has no profiles."),
        };
    }
}
