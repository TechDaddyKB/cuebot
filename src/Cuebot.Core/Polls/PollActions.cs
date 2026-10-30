using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Polls;

/// <summary>
/// Registration for polls: the <see cref="PollTracker"/> (platform modules add <see cref="IPollAdapter"/>s), Cuebot's
/// own <see cref="ChatPolls"/> (chat-polls.json), their sub-actions and triggers. The app creates the tracker at startup.
/// </summary>
public static class PollFeatures
{
    public const string Category = "Polls";

    public static IServiceCollection AddPollFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new PollTracker(sp.GetRequiredService<EventBus>(), sp.GetRequiredService<OverlayFeed>(),
            sp.GetServices<IPollAdapter>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ChatPolls(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<PollTracker>(), sp.GetService<PointsService>()));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<ChatPolls>());
        services.AddSingleton<ISubActionHandler, StartChatPollSubAction>();
        services.AddSingleton<ISubActionHandler, EndChatPollSubAction>();
        services.AddSingleton<ISubActionHandler, StartChatPredictionSubAction>();
        services.AddSingleton<ISubActionHandler, ChatPredictionSubAction>();
        services.AddSingleton<ITriggerProvider, PollTriggers>();
        return services;
    }

    internal static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static void Write(ActionRunContext context, PollState? state)
    {
        context.Args["success"] = state is not null;
        context.Args["pollId"] = state?.Id ?? "";
        context.Args["totalVotes"] = state?.TotalVotes ?? 0;
        context.Args["winningChoice"] = state is { Winner: >= 0 } s ? s.Choices[s.Winner].Title : "";
    }
}

/// <summary>Starts a Cuebot chat poll (voted with the vote command on every platform).</summary>
public sealed class StartChatPollSubAction(ChatPolls polls) : ISubActionHandler
{
    public string TypeId => "polls.start";
    public string Category => PollFeatures.Category;
    public string DisplayName => "Start chat poll";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Question"),
        new("choices", "Choices", ParameterKind.Multiline) { Help = "One per line (2 to 10); viewers vote with !vote 1, !vote 2..." },
        new("duration", "Seconds (0: until ended)", ParameterKind.Number) { DefaultValue = "60" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        PollFeatures.Write(context, polls.StartPoll(p["title"], PollFeatures.Lines(p["choices"]), p.GetInt("duration", 60)));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Ends the running Cuebot chat poll now (with its result).</summary>
public sealed class EndChatPollSubAction(ChatPolls polls) : ISubActionHandler
{
    public string TypeId => "polls.end";
    public string Category => PollFeatures.Category;
    public string DisplayName => "End chat poll";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        PollFeatures.Write(context, polls.EndPoll());
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Starts a Cuebot prediction: viewers bet Cuebot points on an outcome with the bet command.</summary>
public sealed class StartChatPredictionSubAction(ChatPolls polls) : ISubActionHandler
{
    public string TypeId => "polls.startPrediction";
    public string Category => PollFeatures.Category;
    public string DisplayName => "Start chat prediction";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Question"),
        new("outcomes", "Outcomes", ParameterKind.Multiline) { Help = "One per line (2 to 10); viewers bet with !bet 1 500 or !bet 2 all" },
        new("window", "Seconds taking bets (0: until locked)", ParameterKind.Number) { DefaultValue = "120" },
        new("currency", "Points currency") { Help = "Leave empty for the default currency" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        PollFeatures.Write(context, polls.StartPrediction(p["title"], PollFeatures.Lines(p["outcomes"]), p.GetInt("window", 120), p["currency"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Locks, resolves (pays out) or cancels (refunds) the Cuebot prediction.</summary>
public sealed class ChatPredictionSubAction(ChatPolls polls) : ISubActionHandler
{
    internal const string Lock = "Lock";
    internal const string Resolve = "Resolve";
    internal const string Cancel = "Cancel";

    public string TypeId => "polls.prediction";
    public string Category => PollFeatures.Category;
    public string DisplayName => "Lock, resolve or cancel chat prediction";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Do", ParameterKind.Choice) { DefaultValue = Lock, Choices = [Lock, Resolve, Cancel], Help = "Cancel refunds every bet" },
        new("winner", "Winning outcome (to resolve)") { Help = "Its number (1, 2...) or title" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var state = p["mode"].Trim().ToLowerInvariant() switch
        {
            "resolve" => polls.ResolvePrediction(Winner(p["winner"])),
            "cancel" => polls.CancelPrediction(),
            _ => polls.LockPrediction(),
        };
        PollFeatures.Write(context, state);
        return Task.FromResult(SubActionResult.Next);
    }

    private int Winner(string text)
    {
        var outcomes = polls.PredictionOutcomes;
        var winner = ChatPolls.Find(outcomes, text);
        return winner >= 0 || outcomes.Count == 0
            ? winner
            : throw new InvalidOperationException($"'{text}' is not one of the outcomes ({string.Join(", ", outcomes)})");
    }
}

/// <summary>Triggers for Cuebot's own polls and predictions.</summary>
internal sealed class PollTriggers : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(PollEvents.PollStarted, PollFeatures.Category, "Chat poll started", PollEvents.PollStarted),
        new EventTriggerType(PollEvents.PollEnded, PollFeatures.Category, "Chat poll ended", PollEvents.PollEnded),
        new EventTriggerType(PollEvents.PredictionStarted, PollFeatures.Category, "Chat prediction started", PollEvents.PredictionStarted),
        new EventTriggerType(PollEvents.PredictionLocked, PollFeatures.Category, "Chat prediction locked", PollEvents.PredictionLocked),
        new EventTriggerType(PollEvents.PredictionResolved, PollFeatures.Category, "Chat prediction resolved", PollEvents.PredictionResolved),
        new EventTriggerType(PollEvents.PredictionCanceled, PollFeatures.Category, "Chat prediction cancelled", PollEvents.PredictionCanceled),
    ];
}
