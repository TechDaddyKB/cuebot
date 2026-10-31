using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Polls;

/// <summary>A running Cuebot chat poll: who voted for which choice (by "platform:userId"; a new vote replaces the old).</summary>
public sealed class ChatPollRun
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Choices { get; set; } = [];
    public DateTimeOffset? EndsAt { get; set; }
    public Dictionary<string, int> Votes { get; set; } = [];
}

/// <summary>One viewer's bet in a Cuebot prediction.</summary>
public sealed class ChatBet
{
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public int Outcome { get; set; }
    public long Amount { get; set; }
}

/// <summary>A Cuebot prediction not yet resolved or cancelled: its bets (points already taken) by "platform:userId".</summary>
public sealed class ChatPredictionRun
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Outcomes { get; set; } = [];
    public DateTimeOffset? LocksAt { get; set; }
    public bool Locked { get; set; }
    public string Currency { get; set; } = "";
    public Dictionary<string, ChatBet> Bets { get; set; } = [];
}

/// <summary>chat-polls.json: the chat commands and whatever poll or prediction is running.</summary>
public sealed class ChatPollDocument
{
    public string VoteCommand { get; set; } = "!vote";
    public string BetCommand { get; set; } = "!bet";

    /// <summary>A chat message that is only a choice's number ("2") counts as a vote too.</summary>
    public bool NumbersVote { get; set; }

    public ChatPollRun? Poll { get; set; }
    public ChatPredictionRun? Prediction { get; set; }
}

/// <summary>
/// Cuebot's own polls and predictions, voted on in chat on every platform (Kick and Rumble have none of their own):
/// <c>!vote 2</c> or <c>!vote choice</c> for polls, <c>!bet outcome amount</c> (or <c>all</c>) for predictions, which
/// take Cuebot points (<see cref="PointsService"/>) and pay winners from the whole pot in proportion to their bets.
/// Cancelling refunds every bet. A poll ends, and a prediction locks, when its time is up (or when told to). The
/// running poll and prediction are kept in chat-polls.json, so bets survive a restart. States go to the
/// <see cref="PollTracker"/> as source "cuebot"; starts and ends are published as <see cref="PollEvents"/>.
/// </summary>
public sealed class ChatPolls : IPeriodicService, IDisposable
{
    public const string Source = "cuebot";
    public const int MinChoices = 2;
    public const int MaxChoices = 10;

    private readonly object _gate = new();
    private readonly JsonFile<ChatPollDocument>? _file;
    private readonly ChatPollDocument _memory = new();
    private readonly PollTracker _tracker;
    private readonly EventBus _bus;
    private readonly PointsService? _points;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;

    public ChatPolls(AppPaths? paths, AppLog log, EventBus bus, PollTracker tracker, PointsService? points = null, TimeProvider? time = null)
    {
        _bus = bus;
        _tracker = tracker;
        _points = points;
        _time = time ?? TimeProvider.System;
        if (paths is not null) _file = new JsonFile<ChatPollDocument>(paths.File("chat-polls.json"), log);
        lock (_gate)
        {
            if (Doc.Poll is { } poll) _tracker.Set(ToPollState(poll, PollState.Active));
            if (Doc.Prediction is { } prediction) _tracker.Set(ToPredictionState(prediction, prediction.Locked ? PollState.Locked : PollState.Active));
        }
        _subscription = bus.Subscribe(OnEvent);
    }

    private ChatPollDocument Doc => _file?.Value ?? _memory;

    public string VoteCommand
    {
        get { lock (_gate) return Doc.VoteCommand; }
    }

    public string BetCommand
    {
        get { lock (_gate) return Doc.BetCommand; }
    }

    /// <summary>Whether a bare number in chat ("2") votes in a running poll.</summary>
    public bool NumbersVote
    {
        get { lock (_gate) return Doc.NumbersVote; }
    }

    /// <summary>Changes the chat commands (empty keeps the old one) and whether a bare number votes.</summary>
    public void SetCommands(string? voteCommand, string? betCommand, bool? numbersVote)
    {
        lock (_gate)
        {
            if (Command(voteCommand) is { Length: > 0 } vote) Doc.VoteCommand = vote;
            if (Command(betCommand) is { Length: > 0 } bet) Doc.BetCommand = bet;
            if (numbersVote is { } numbers) Doc.NumbersVote = numbers;
            Save();
        }
    }

    public bool PollRunning
    {
        get { lock (_gate) return Doc.Poll is not null; }
    }

    /// <summary>The unresolved prediction's outcomes (empty when there is none).</summary>
    public IReadOnlyList<string> PredictionOutcomes
    {
        get { lock (_gate) return Doc.Prediction?.Outcomes.ToArray() ?? []; }
    }

    public bool PredictionLocked
    {
        get { lock (_gate) return Doc.Prediction?.Locked ?? false; }
    }

    // ---- Polls ------------------------------------------------------------------------------------------------------

    /// <summary>Starts a poll; <paramref name="seconds"/> 0 runs until <see cref="EndPoll"/>. Throws when one is running or the input is bad.</summary>
    public PollState StartPoll(string title, IEnumerable<string> choices, int seconds)
    {
        var list = Clean(choices);
        Validate(title, list);
        PollState state;
        lock (_gate)
        {
            if (Doc.Poll is not null) throw new InvalidOperationException("A chat poll is already running; end it first");
            var poll = new ChatPollRun
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title.Trim(),
                Choices = list,
                EndsAt = seconds > 0 ? _time.GetUtcNow().AddSeconds(seconds) : null,
            };
            Doc.Poll = poll;
            Save();
            state = ToPollState(poll, PollState.Active);
        }
        _tracker.Set(state);
        var args = Args("pollId", state, "choice");
        args["duration"] = Math.Max(0, seconds);
        args["command"] = state.Command;
        _bus.Publish(new BotEvent(PollEvents.PollStarted, args));
        return state;
    }

    /// <summary>Ends the running poll and announces the result; null when none is running.</summary>
    public PollState? EndPoll(bool canceled = false)
    {
        PollState state;
        lock (_gate)
        {
            if (Doc.Poll is not { } poll) return null;
            Doc.Poll = null;
            Save();
            state = ToPollState(poll, canceled ? PollState.Canceled : PollState.Ended);
        }
        _tracker.Set(state);
        var args = Args("pollId", state, "choice");
        args["totalVotes"] = state.TotalVotes;
        args["winningChoice"] = state.Winner >= 0 ? state.Choices[state.Winner].Title : "";
        args["winningVotes"] = state.Winner >= 0 ? state.Choices[state.Winner].Votes : 0;
        args["canceled"] = canceled;
        _bus.Publish(new BotEvent(PollEvents.PollEnded, args));
        return state;
    }

    // ---- Predictions ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a prediction taking bets for <paramref name="seconds"/> (0: until <see cref="LockPrediction"/>) in a points
    /// currency (empty: the default). Throws when points are not available, one is unresolved or the input is bad.
    /// </summary>
    public PollState StartPrediction(string title, IEnumerable<string> outcomes, int seconds, string? currency = null)
    {
        if (_points is null) throw new InvalidOperationException("Predictions need Cuebot points");
        var list = Clean(outcomes);
        Validate(title, list);
        currency = string.IsNullOrWhiteSpace(currency) ? "" : currency.Trim();
        if (currency.Length > 0 && !_points.CurrencyExists(currency)) throw new ArgumentException($"There is no points currency '{currency}'");
        PollState state;
        lock (_gate)
        {
            if (Doc.Prediction is not null) throw new InvalidOperationException("A chat prediction is still open; resolve or cancel it first");
            var prediction = new ChatPredictionRun
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title.Trim(),
                Outcomes = list,
                LocksAt = seconds > 0 ? _time.GetUtcNow().AddSeconds(seconds) : null,
                Currency = currency,
            };
            Doc.Prediction = prediction;
            Save();
            state = ToPredictionState(prediction, PollState.Active);
        }
        _tracker.Set(state);
        var args = Args("predictionId", state, "outcome");
        args["window"] = Math.Max(0, seconds);
        args["currency"] = state.Currency;
        args["command"] = state.Command;
        _bus.Publish(new BotEvent(PollEvents.PredictionStarted, args));
        return state;
    }

    /// <summary>Stops taking bets; null when there is no open prediction (or it is already locked).</summary>
    public PollState? LockPrediction()
    {
        PollState state;
        lock (_gate)
        {
            if (Doc.Prediction is not { Locked: false } prediction) return null;
            prediction.Locked = true;
            Save();
            state = ToPredictionState(prediction, PollState.Locked);
        }
        _tracker.Set(state);
        _bus.Publish(new BotEvent(PollEvents.PredictionLocked, PredictionArgs(state)));
        return state;
    }

    /// <summary>
    /// Pays out: each winner gets their bet's share of the whole pot (rounded down, never less than they bet), everyone
    /// else loses theirs. Null when there is no unresolved prediction; throws for a winner that is not an outcome.
    /// </summary>
    public PollState? ResolvePrediction(int winner)
    {
        PollState state;
        List<(ChatBet Bet, long Payout)> payouts;
        string currency;
        lock (_gate)
        {
            if (Doc.Prediction is not { } prediction) return null;
            if (winner < 0 || winner >= prediction.Outcomes.Count) throw new ArgumentException($"Choose an outcome from 1 to {prediction.Outcomes.Count}");
            var pot = prediction.Bets.Values.Sum(b => b.Amount);
            var winningPool = prediction.Bets.Values.Where(b => b.Outcome == winner).Sum(b => b.Amount);
            payouts = prediction.Bets.Values.Where(b => b.Outcome == winner)
                .Select(b => (b, Math.Max(b.Amount, (long)Math.Floor((decimal)b.Amount * pot / winningPool)))).ToList();
            currency = prediction.Currency;
            Doc.Prediction = null;
            Save();
            state = ToPredictionState(prediction, PollState.Ended) with { Winner = winner };
        }
        foreach (var (bet, payout) in payouts)
            _points!.Add(bet.Platform, bet.UserId, bet.UserName, payout, "prediction won", Currency(currency));
        _tracker.Set(state);
        var args = PredictionArgs(state);
        args["winningOutcome"] = state.Choices[winner].Title;
        args["winnerCount"] = payouts.Count;
        args["paidOut"] = payouts.Sum(p => p.Payout);
        _bus.Publish(new BotEvent(PollEvents.PredictionResolved, args));
        return state;
    }

    /// <summary>Refunds every bet; null when there is no unresolved prediction.</summary>
    public PollState? CancelPrediction()
    {
        PollState state;
        List<ChatBet> bets;
        string currency;
        lock (_gate)
        {
            if (Doc.Prediction is not { } prediction) return null;
            bets = prediction.Bets.Values.ToList();
            currency = prediction.Currency;
            Doc.Prediction = null;
            Save();
            state = ToPredictionState(prediction, PollState.Canceled);
        }
        foreach (var bet in bets) _points?.Add(bet.Platform, bet.UserId, bet.UserName, bet.Amount, "prediction refund", Currency(currency));
        _tracker.Set(state);
        _bus.Publish(new BotEvent(PollEvents.PredictionCanceled, PredictionArgs(state)));
        return state;
    }

    /// <summary>An outcome or choice by its number (from 1) or title; -1 when none matches.</summary>
    public static int Find(IReadOnlyList<string> titles, string text)
    {
        text = text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n >= 1 && n <= titles.Count ? n - 1 : -1;
        for (var i = 0; i < titles.Count; i++)
            if (string.Equals(titles[i], text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- Chat -------------------------------------------------------------------------------------------------------

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent || e.Args.GetValueOrDefault("isInternal") is true) return;
        var platform = e.Args.GetString("platform");
        var userId = e.Args.GetString("userId");
        var user = e.Args.GetString("user");
        var text = e.Args.GetString("message").Trim();
        if (platform.Length == 0 || text.Length == 0) return;
        if (userId.Length == 0) userId = user.ToLowerInvariant();
        if (userId.Length == 0) return;
        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        PollState? changed = null;
        lock (_gate)
        {
            if (Doc.Poll is { } poll)
            {
                var choice = words[0].Equals(Doc.VoteCommand, StringComparison.OrdinalIgnoreCase) && words.Length == 2 ? Find(poll.Choices, words[1])
                    : Doc.NumbersVote && words.Length == 1 && words[0].All(char.IsAsciiDigit) ? Find(poll.Choices, words[0])
                    : -1;
                if (choice >= 0)
                {
                    poll.Votes[$"{platform.ToLowerInvariant()}:{userId}"] = choice;
                    Save();
                    changed = ToPollState(poll, PollState.Active);
                }
            }
            if (changed is null && Doc.Prediction is { Locked: false } prediction && words.Length == 2
                && words[0].Equals(Doc.BetCommand, StringComparison.OrdinalIgnoreCase))
            {
                changed = Bet(prediction, platform, userId, user, words[1]);
            }
        }
        if (changed is not null) _tracker.Set(changed);
    }

    /// <summary>"outcome amount": takes the points and records the bet; null when it does not count.</summary>
    private PollState? Bet(ChatPredictionRun prediction, string platform, string userId, string user, string rest)
    {
        if (_points is null) return null;
        var at = rest.LastIndexOf(' ');
        if (at <= 0) return null;
        var outcome = Find(prediction.Outcomes, rest[..at]);
        var amountText = rest[(at + 1)..].Trim();
        if (outcome < 0) return null;
        var key = $"{platform.ToLowerInvariant()}:{userId}";
        if (prediction.Bets.TryGetValue(key, out var earlier) && earlier.Outcome != outcome) return null;   // one side only
        var currency = Currency(prediction.Currency);
        var amount = amountText.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? _points.Get(platform, userId, currency)
            : long.TryParse(amountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (amount <= 0 || !_points.TrySpend(platform, userId, user, amount, out _, currency)) return null;
        var bet = earlier ?? (prediction.Bets[key] = new ChatBet { Platform = platform, UserId = userId, UserName = user, Outcome = outcome });
        bet.Amount += amount;
        Save();
        return ToPredictionState(prediction, PollState.Active);
    }

    /// <summary>Ends a poll and locks a prediction once their time is up.</summary>
    public void Tick(DateTimeOffset now)
    {
        bool endPoll, lockPrediction;
        lock (_gate)
        {
            endPoll = Doc.Poll?.EndsAt is { } ends && now >= ends;
            lockPrediction = Doc.Prediction is { Locked: false, LocksAt: { } locks } && now >= locks;
        }
        if (endPoll) EndPoll();
        if (lockPrediction) LockPrediction();
    }

    // ---- States -----------------------------------------------------------------------------------------------------

    private PollState ToPollState(ChatPollRun poll, string status)
    {
        var choices = poll.Choices.Select((c, i) => new PollChoice((i + 1).ToString(CultureInfo.InvariantCulture), c, poll.Votes.Values.Count(v => v == i))).ToArray();
        var winner = status == PollState.Active || choices.All(c => c.Votes == 0) ? -1 : Array.IndexOf(choices, choices.MaxBy(c => c.Votes));
        return new PollState(Source, PollState.Poll, poll.Id, poll.Title, status, choices, winner, poll.EndsAt, Command: Doc.VoteCommand);
    }

    private PollState ToPredictionState(ChatPredictionRun prediction, string status)
    {
        var choices = prediction.Outcomes.Select((o, i) =>
        {
            var bets = prediction.Bets.Values.Where(b => b.Outcome == i).ToArray();
            return new PollChoice((i + 1).ToString(CultureInfo.InvariantCulture), o, bets.Length, bets.Sum(b => b.Amount));
        }).ToArray();
        var currency = _points is null ? prediction.Currency : _points.NameOf(Currency(prediction.Currency));
        return new PollState(Source, PollState.Prediction, prediction.Id, prediction.Title, status, choices, -1, prediction.LocksAt,
            currency, Doc.BetCommand);
    }

    private static Dictionary<string, object?> Args(string idKey, PollState state, string prefix)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [idKey] = state.Id,
            ["title"] = state.Title,
            [prefix + "Count"] = state.Choices.Count,
            ["source"] = Source,
        };
        for (var i = 0; i < state.Choices.Count; i++)
        {
            args[$"{prefix}{i}Title"] = state.Choices[i].Title;
            args[$"{prefix}{i}Votes"] = state.Choices[i].Votes;
        }
        return args;
    }

    private static Dictionary<string, object?> PredictionArgs(PollState state)
    {
        var args = Args("predictionId", state, "outcome");
        for (var i = 0; i < state.Choices.Count; i++)
        {
            args[$"outcome{i}Users"] = state.Choices[i].Votes;
            args[$"outcome{i}Points"] = state.Choices[i].Points;
        }
        args["totalUsers"] = state.TotalVotes;
        args["totalPoints"] = state.TotalPoints;
        args["currency"] = state.Currency;
        return args;
    }

    private static string? Currency(string currency) => currency.Length == 0 ? null : currency;

    private static string? Command(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : command.Trim().Split(' ')[0];

    private static List<string> Clean(IEnumerable<string> choices) =>
        choices.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    private static void Validate(string title, List<string> choices)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A title is needed");
        if (choices.Count is < MinChoices or > MaxChoices) throw new ArgumentException($"Give {MinChoices} to {MaxChoices} choices");
        if (choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != choices.Count) throw new ArgumentException("Choices must differ");
    }

    private void Save() => _file?.ScheduleSave();

    public void Dispose()
    {
        _subscription.Dispose();
        _file?.Dispose();
    }
}
