using System.Globalization;
using Cuebot.Core.Events;
using Cuebot.Core.Polls;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.Twitch;

/// <summary>Twitch polls and predictions (EventSub) as <see cref="PollState"/>s for overlay widgets.</summary>
public sealed class TwitchPollAdapter : IPollAdapter
{
    public PollState? ToPoll(BotEvent e)
    {
        var a = e.Args;
        switch (e.Type)
        {
            case TwitchEventTypes.PollCreated or TwitchEventTypes.PollUpdated or TwitchEventTypes.PollCompleted or TwitchEventTypes.PollTerminated:
            {
                var open = e.Type is TwitchEventTypes.PollCreated or TwitchEventTypes.PollUpdated;
                var choices = Enumerable.Range(0, (int)Number(a, "choiceCount"))
                    .Select(i => new PollChoice(Str(a, $"choice{i}Id"), Str(a, $"choice{i}Title"), (long)Number(a, $"choice{i}Votes")))
                    .ToArray();
                var winner = open || choices.All(c => c.Votes == 0) ? -1 : Array.IndexOf(choices, choices.MaxBy(c => c.Votes));
                return new PollState("twitch", PollState.Poll, Str(a, "pollId"), Str(a, "title"), open ? PollState.Active : PollState.Ended,
                    choices, winner, Time(Str(a, "endsAt")));
            }
            case TwitchEventTypes.PredictionCreated or TwitchEventTypes.PredictionUpdated or TwitchEventTypes.PredictionLocked
                or TwitchEventTypes.PredictionCompleted or TwitchEventTypes.PredictionCanceled:
            {
                var outcomes = Enumerable.Range(0, (int)Number(a, "outcomeCount"))
                    .Select(i => new PollChoice(Str(a, $"outcome{i}Id"), Str(a, $"outcome{i}Title"), (long)Number(a, $"outcome{i}Users"),
                        (long)Number(a, $"outcome{i}ChannelPoints")))
                    .ToArray();
                var status = e.Type switch
                {
                    TwitchEventTypes.PredictionLocked => PollState.Locked,
                    TwitchEventTypes.PredictionCompleted => PollState.Ended,
                    TwitchEventTypes.PredictionCanceled => PollState.Canceled,
                    _ => PollState.Active,
                };
                var winningId = Str(a, "winningOutcomeId");
                var winner = winningId.Length > 0 ? Array.FindIndex(outcomes, o => o.Id == winningId) : -1;
                return new PollState("twitch", PollState.Prediction, Str(a, "predictionId"), Str(a, "title"), status, outcomes, winner,
                    Time(Str(a, "locksAt")), "Channel Points");
            }
            default:
                return null;
        }
    }

    private static DateTimeOffset? Time(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
