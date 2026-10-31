using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Polls;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The Overlays page's "Chat poll and prediction" panel: starts and ends Cuebot chat polls (voted on every platform)
/// and runs Cuebot predictions with points. The Poll and prediction widget shows them on overlays.
/// </summary>
public sealed partial class ChatPollPanelViewModel : ObservableObject
{
    private readonly ChatPolls _polls;
    private readonly PollTracker _tracker;

    [ObservableProperty] private string _pollTitle = "";
    [ObservableProperty] private string _pollChoices = "";
    [ObservableProperty] private decimal _pollSeconds = 60;
    [ObservableProperty] private string _predictionTitle = "";
    [ObservableProperty] private string _predictionOutcomes = "";
    [ObservableProperty] private decimal _predictionSeconds = 120;
    [ObservableProperty] private string? _winner;
    [ObservableProperty] private string _message = "";

    public ChatPollPanelViewModel(ChatPolls polls, PollTracker tracker)
    {
        _polls = polls;
        _tracker = tracker;
        tracker.Changed += state =>
        {
            if (state.Source == ChatPolls.Source) Dispatch(Refresh);
        };
        Refresh();
    }

    /// <summary>Runs UI updates on the UI thread (tests replace it).</summary>
    internal static Action<Action> Dispatch { get; set; } = action =>
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    };

    public string Commands => $"Viewers vote with {_polls.VoteCommand} 1, {_polls.VoteCommand} 2… and bet Cuebot points with {_polls.BetCommand} 1 500 (or all).";

    public bool PollRunning => _polls.PollRunning;
    public bool PredictionOpen => Outcomes.Count > 0;
    public bool PredictionLocked => _polls.PredictionLocked;
    public bool CanStartPrediction => !PredictionOpen;
    public bool CanLock => PredictionOpen && !PredictionLocked;

    /// <summary>The running poll with its votes so far, however it was started (here, an action, the API or MCP); "" when none.</summary>
    public string PollStatus => Status(PollState.Poll);

    /// <summary>The open prediction with its bets so far; "" when none.</summary>
    public string PredictionStatus => Status(PollState.Prediction);

    private string Status(string kind)
    {
        if (_tracker.Current.FirstOrDefault(p => p.Source == ChatPolls.Source && p.Kind == kind && p.IsOpen) is not { } state) return "";
        var choices = string.Join(", ", state.Choices.Select((c, i) =>
            $"{i + 1}. {c.Title} ({(kind == PollState.Prediction ? c.Points + " pts" : c.Votes.ToString(System.Globalization.CultureInfo.CurrentCulture))})"));
        return $"{(state.Status == PollState.Locked ? "Locked" : "Running")}: {state.Title} · {choices}";
    }

    /// <summary>The open prediction's outcomes, to pick the winner from.</summary>
    public ObservableCollection<string> Outcomes { get; } = [];

    private void Refresh()
    {
        var outcomes = _polls.PredictionOutcomes;
        if (!outcomes.SequenceEqual(Outcomes))
        {
            Outcomes.Clear();
            foreach (var o in outcomes) Outcomes.Add(o);
            Winner = null;
        }
        foreach (var name in (string[])[nameof(PollRunning), nameof(PredictionOpen), nameof(PredictionLocked), nameof(CanStartPrediction), nameof(CanLock),
                     nameof(PollStatus), nameof(PredictionStatus)])
            OnPropertyChanged(name);
    }

    private void Try(Func<PollState?> run, string done)
    {
        try
        {
            Message = run() is null ? "There was nothing to do." : done;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        Refresh();
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [RelayCommand]
    private void StartPoll() => Try(() => _polls.StartPoll(PollTitle, Lines(PollChoices), (int)PollSeconds),
        PollSeconds > 0 ? $"Poll started for {PollSeconds:0} seconds." : "Poll started; end it when you are ready.");

    [RelayCommand]
    private void EndPoll() => Try(() => _polls.EndPoll(), "Poll ended.");

    [RelayCommand]
    private void StartPrediction() => Try(() => _polls.StartPrediction(PredictionTitle, Lines(PredictionOutcomes), (int)PredictionSeconds),
        "Prediction started; viewers can bet now.");

    [RelayCommand]
    private void LockPrediction() => Try(() => _polls.LockPrediction(), "Bets locked.");

    [RelayCommand]
    private void ResolvePrediction()
    {
        var index = Winner is null ? -1 : Outcomes.IndexOf(Winner);
        if (index < 0)
        {
            Message = "Pick the winning outcome first.";
            return;
        }
        Try(() => _polls.ResolvePrediction(index), $"\"{Winner}\" won; winners have been paid.");
    }

    [RelayCommand]
    private void CancelPrediction() => Try(() => _polls.CancelPrediction(), "Prediction cancelled; every bet was refunded.");
}
