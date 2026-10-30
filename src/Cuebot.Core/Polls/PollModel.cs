using System.Globalization;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;

namespace Cuebot.Core.Polls;

/// <summary>One choice of a poll or outcome of a prediction: votes (people) and, for predictions, points bet on it.</summary>
public sealed record PollChoice(string Id, string Title, long Votes, long Points = 0);

/// <summary>
/// A poll or prediction from any source, in one shape for overlays and the app. <see cref="Source"/> is "twitch",
/// "youtube" or "cuebot" (Cuebot's own chat polls and predictions); <see cref="Kind"/> is "poll" or "prediction";
/// <see cref="Status"/> is "active", "locked" (predictions taking no more bets), "ended" or "canceled".
/// <see cref="Winner"/> is the winning choice's index once known (-1 otherwise).
/// </summary>
public sealed record PollState(string Source, string Kind, string Id, string Title, string Status, IReadOnlyList<PollChoice> Choices,
    int Winner = -1, DateTimeOffset? EndsAt = null, string Currency = "", string Command = "")
{
    public const string Poll = "poll";
    public const string Prediction = "prediction";
    public const string Active = "active";
    public const string Locked = "locked";
    public const string Ended = "ended";
    public const string Canceled = "canceled";

    public bool IsOpen => Status is Active or Locked;
    public long TotalVotes => Choices.Sum(c => c.Votes);
    public long TotalPoints => Choices.Sum(c => c.Points);

    /// <summary>
    /// <c>{source, kind, id, title, status, choices: [{id, title, votes, points, percent}], winner, endsAt, totalVotes,
    /// totalPoints, currency, command}</c> (what overlay widgets get; <c>percent</c> of votes, or of points for predictions).
    /// </summary>
    public JsonObject ToJson()
    {
        var byPoints = Kind == Prediction && TotalPoints > 0;
        double total = byPoints ? TotalPoints : TotalVotes;
        return new JsonObject
        {
            ["source"] = Source,
            ["kind"] = Kind,
            ["id"] = Id,
            ["title"] = Title,
            ["status"] = Status,
            ["choices"] = new JsonArray([.. Choices.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["votes"] = c.Votes,
                ["points"] = c.Points,
                ["percent"] = total > 0 ? Math.Round((byPoints ? c.Points : c.Votes) / total * 100, 1) : 0,
            })]),
            ["winner"] = Winner,
            ["endsAt"] = EndsAt?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["totalVotes"] = TotalVotes,
            ["totalPoints"] = TotalPoints,
            ["currency"] = Currency,
            ["command"] = Command,
        };
    }
}

/// <summary>
/// Turns a platform module's poll and prediction events into <see cref="PollState"/>s (null for any other event).
/// Twitch and YouTube register one; Cuebot's own polls report to the <see cref="PollTracker"/> directly.
/// </summary>
public interface IPollAdapter
{
    PollState? ToPoll(BotEvent e);
}

/// <summary>Events from Cuebot's own chat polls and predictions (see <see cref="ChatPolls"/>).</summary>
public static class PollEvents
{
    /// <summary>Args: pollId, title, choiceCount, choice{i}Title, duration (seconds), command.</summary>
    public const string PollStarted = "cuebot.pollStarted";

    /// <summary>Args: pollId, title, totalVotes, winningChoice, winningVotes, choice{i}Title, choice{i}Votes; canceled.</summary>
    public const string PollEnded = "cuebot.pollEnded";

    /// <summary>Args: predictionId, title, outcomeCount, outcome{i}Title, window (seconds), currency, command.</summary>
    public const string PredictionStarted = "cuebot.predictionStarted";

    /// <summary>Args as <see cref="PredictionStarted"/> plus totalUsers, totalPoints, outcome{i}Users, outcome{i}Points.</summary>
    public const string PredictionLocked = "cuebot.predictionLocked";

    /// <summary>Args as <see cref="PredictionLocked"/> plus winningOutcome, winnerCount, paidOut.</summary>
    public const string PredictionResolved = "cuebot.predictionResolved";

    /// <summary>Args as <see cref="PredictionLocked"/> (every bet was refunded).</summary>
    public const string PredictionCanceled = "cuebot.predictionCanceled";
}
