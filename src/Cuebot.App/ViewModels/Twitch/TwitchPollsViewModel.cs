using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>Polls and predictions tab: create, end, lock, resolve and cancel.</summary>
public sealed partial class TwitchPollsViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    [ObservableProperty] private string _pollTitle = "";
    [ObservableProperty] private string _pollChoices = "";
    [ObservableProperty] private decimal _pollDuration = 60;
    [ObservableProperty] private decimal _pollPointsPerVote;
    [ObservableProperty] private string _pollStatus = "No poll loaded";

    [ObservableProperty] private string _predictionTitle = "";
    [ObservableProperty] private string _predictionOutcomes = "";
    [ObservableProperty] private decimal _predictionWindow = 120;
    [ObservableProperty] private string _predictionStatus = "No prediction loaded";
    [ObservableProperty] private string? _winningOutcome;

    /// <summary>Outcome titles of the latest prediction, to pick the winner from.</summary>
    public ObservableCollection<string> Outcomes { get; } = [];

    internal static string[] Lines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private Task CreatePoll() => Run(async () =>
    {
        var poll = await Api.CreatePollAsync(PollTitle, Lines(PollChoices), (int)PollDuration, (int)PollPointsPerVote);
        ShowPoll(poll);
    }, "Poll started.");

    [RelayCommand]
    private Task EndPoll() => Run(async () => ShowPoll(await Api.EndPollAsync()), "Poll ended.");

    [RelayCommand]
    private Task ArchivePoll() => Run(async () => ShowPoll(await Api.EndPollAsync(archive: true)), "Poll archived.");

    [RelayCommand]
    private Task LoadPoll() => Run(async () => ShowPoll(await Api.GetLatestPollAsync()));

    private void ShowPoll(TwitchPoll? poll) => PollStatus = poll is null
        ? "No poll yet"
        : $"{poll.Title} [{poll.Status}]: " + string.Join(", ", poll.Choices.Select(c => $"{c.Title} {c.Votes}"));

    [RelayCommand]
    private Task CreatePrediction() => Run(async () =>
        ShowPrediction(await Api.CreatePredictionAsync(PredictionTitle, Lines(PredictionOutcomes), (int)PredictionWindow)), "Prediction started.");

    [RelayCommand]
    private Task LockPrediction() => Run(async () => ShowPrediction(await Api.LockPredictionAsync()), "Prediction locked.");

    [RelayCommand]
    private Task CancelPrediction() => Run(async () => ShowPrediction(await Api.CancelPredictionAsync()), "Prediction cancelled; points refunded.");

    [RelayCommand]
    private Task ResolvePrediction() => Run(async () =>
    {
        if (string.IsNullOrEmpty(WinningOutcome)) throw new InvalidOperationException("Pick the winning outcome first (load the prediction).");
        ShowPrediction(await Api.ResolvePredictionAsync(WinningOutcome));
    }, "Prediction resolved.");

    [RelayCommand]
    private Task LoadPrediction() => Run(async () => ShowPrediction(await Api.GetLatestPredictionAsync()));

    private void ShowPrediction(TwitchPrediction? prediction)
    {
        Outcomes.Clear();
        if (prediction is null)
        {
            PredictionStatus = "No prediction yet";
            return;
        }
        foreach (var outcome in prediction.Outcomes) Outcomes.Add(outcome.Title);
        WinningOutcome = Outcomes.FirstOrDefault();
        PredictionStatus = $"{prediction.Title} [{prediction.Status}]: "
                           + string.Join(", ", prediction.Outcomes.Select(o => $"{o.Title} ({o.Users} users, {o.ChannelPoints} points)"));
    }
}
