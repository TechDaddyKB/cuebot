using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>One custom Power-up as Twitch lists it.</summary>
public sealed class TwitchPowerUpRow(TwitchCustomPowerUp p)
{
    public string Title => p.Title;
    public string Prompt => p.Prompt;
    public bool HasPrompt => p.Prompt.Length > 0;

    public string Details => string.Join(" · ", new[]
    {
        $"{p.Bits} bits",
        p.IsEnabled ? p.IsPaused ? "paused" : "enabled" : "disabled",
        p.IsInStock ? "" : "out of stock",
        p.IsUserInputRequired ? "viewer input" : "",
        p.GlobalCooldownSeconds > 0 ? $"cooldown {p.GlobalCooldownSeconds} s" : "",
        p.MaxPerStream > 0 ? $"max {p.MaxPerStream} per stream" : "",
        p.MaxPerUserPerStream > 0 ? $"max {p.MaxPerUserPerStream} per viewer" : "",
        p.RedeemedThisStream is { } n ? $"{n} this stream" : "",
    }.Where(s => s.Length > 0));
}

/// <summary>
/// Power-ups tab: the channel's custom Power-ups (bought with bits). Twitch's API can only list them, so they are made
/// and changed on the Twitch dashboard; Cuebot shows them, counts them against Twitch's limit and starts actions.
/// </summary>
public sealed partial class TwitchPowerUpsViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    public ObservableCollection<TwitchPowerUpRow> PowerUps { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsedText))] private int? _count;

    /// <summary>"3 of 50 custom Power-ups used": Twitch allows 50 per channel, enabled and disabled.</summary>
    public string UsedText => Count is { } n
        ? $"{n} of {TwitchChannelRewards.MaxPowerUps} custom Power-ups used"
        : $"Twitch allows up to {TwitchChannelRewards.MaxPowerUps} custom Power-ups";

    [RelayCommand]
    private Task Reload() => Run(async () =>
    {
        var powerUps = await Api.GetCustomPowerUpsAsync();
        PowerUps.Clear();
        foreach (var p in powerUps.OrderBy(p => p.Bits)) PowerUps.Add(new TwitchPowerUpRow(p));
        Count = powerUps.Count;
        if (powerUps.Count == 0) Info = "This channel has no custom Power-ups yet.";
    });
}
