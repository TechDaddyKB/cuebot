using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Events;
using Cuebot.Core.Games;
using Cuebot.Core.Storage;
using Cuebot.Integrations;
using Cuebot.Integrations.Steam;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The Game categories page: Steam game detection (with the apps that are never a game) and asking to switch the
/// stream category to the game being played on Twitch, Kick and YouTube.
/// </summary>
public sealed partial class GameCategoriesViewModel : PageViewModel
{
    public const string PageTitle = "Game categories";

    private readonly CategorySwitcher _switcher;
    private readonly JsonFile<IntegrationsSettings> _integrations;
    private readonly SteamGameWatcher? _steam;
    private readonly EventBus _bus;
    private bool _loading;

    public GameCategoriesViewModel(CategorySwitcher switcher, JsonFile<IntegrationsSettings> integrations, EventBus bus, SteamGameWatcher? steam = null)
        : base(PageTitle)
    {
        _switcher = switcher;
        _integrations = integrations;
        _bus = bus;
        _steam = steam;
        Load();
        bus.Subscribe(e =>
        {
            if (e.Type is GameEvents.Started or GameEvents.Stopped) OnUi(() => OnPropertyChanged(nameof(Playing)));
        });
    }

    [ObservableProperty] private bool _detectSteam;
    [ObservableProperty] private string _excluded = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _twitch;
    [ObservableProperty] private bool _kick;
    [ObservableProperty] private bool _youTube;
    [ObservableProperty] private bool _onlyWhileLive;
    [ObservableProperty] private decimal? _requestMinutes;
    [ObservableProperty] private string _returnCategory = "";
    [ObservableProperty] private string _alwaysSwitch = "";
    [ObservableProperty] private string _neverAsk = "";
    [ObservableProperty] private string _overrides = "";
    [ObservableProperty] private string _overridesError = "";
    [ObservableProperty] private string _testGame = "";
    [ObservableProperty] private string _status = "";

    /// <summary>What Steam is running now, for the page.</summary>
    public string Playing => !DetectSteam ? "Steam detection is off."
        : _steam?.Current is { } g ? $"Playing now: {g.Name} (Steam app {g.AppId})" : "No game running on Steam right now.";

    private void Load()
    {
        _loading = true;
        var steam = _integrations.Value.Steam;
        DetectSteam = steam.Enabled;
        Excluded = string.Join(Environment.NewLine, steam.Excluded);
        var s = _switcher.Settings;
        Enabled = s.Enabled;
        Twitch = s.Twitch;
        Kick = s.Kick;
        YouTube = s.YouTube;
        OnlyWhileLive = s.OnlyWhileLive;
        RequestMinutes = s.RequestMinutes;
        ReturnCategory = s.ReturnCategory;
        AlwaysSwitch = string.Join(Environment.NewLine, s.AlwaysSwitch);
        NeverAsk = string.Join(Environment.NewLine, s.NeverAsk);
        Overrides = string.Join(Environment.NewLine, s.Overrides.Select(o => $"{o.Key} = {o.Value}"));
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null or nameof(Status) or nameof(TestGame) or nameof(OverridesError) or nameof(Playing)) return;
        Save();
    }

    internal static List<string> Lines(string text) =>
        [.. text.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>"Game = Category" lines; the error names the first line that is not one.</summary>
    internal static Dictionary<string, string> ParseOverrides(string text, out string error)
    {
        error = "";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.IndexOf('=');
            if (at > 0 && line[(at + 1)..].Trim().Length > 0) result[line[..at].Trim()] = line[(at + 1)..].Trim();
            else if (error.Length == 0) error = $"'{line}' is not Game = Category";
        }
        return result;
    }

    private void Save()
    {
        _integrations.Value.Steam.Enabled = DetectSteam;
        // Excluded names may contain commas only in rare game names; one per line is the documented form.
        _integrations.Value.Steam.Excluded = [.. Excluded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        _integrations.ScheduleSave();
        var overrides = ParseOverrides(Overrides, out var error);
        OverridesError = error;
        _switcher.UpdateSettings(s =>
        {
            s.Enabled = Enabled;
            s.Twitch = Twitch;
            s.Kick = Kick;
            s.YouTube = YouTube;
            s.OnlyWhileLive = OnlyWhileLive;
            s.RequestMinutes = (int)Math.Clamp(RequestMinutes ?? 10, 1, 120);
            s.ReturnCategory = ReturnCategory.Trim();
            s.AlwaysSwitch = Lines(AlwaysSwitch);
            s.NeverAsk = Lines(NeverAsk);
            if (error.Length == 0) s.Overrides = overrides;
        });
        OnPropertyChanged(nameof(Playing));
    }

    /// <summary>Acts as if the game just started, to try the request without launching it.</summary>
    [RelayCommand]
    private void Test()
    {
        var game = TestGame.Trim();
        if (game.Length == 0)
        {
            Status = "Type a game's name first.";
            return;
        }
        _bus.Publish(new BotEvent(GameEvents.Started, new Dictionary<string, object?> { ["game"] = game, ["steamAppId"] = 0L, ["source"] = "test" }));
        Status = $"Looking up {game} on your platforms. If one has it, the request shows at the top of the window in a moment.";
    }
}
