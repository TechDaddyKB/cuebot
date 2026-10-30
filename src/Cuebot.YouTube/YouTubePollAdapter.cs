using Cuebot.Core.Events;
using Cuebot.Core.Polls;
using static Cuebot.Core.Overlays.Events.OverlayAlerts;

namespace Cuebot.YouTube;

/// <summary>YouTube live chat polls as <see cref="PollState"/>s for overlay widgets (YouTube has no predictions).</summary>
public sealed class YouTubePollAdapter : IPollAdapter
{
    public PollState? ToPoll(BotEvent e)
    {
        if (e.Type is not (YouTubeEventTypes.PollStarted or YouTubeEventTypes.PollUpdated or YouTubeEventTypes.PollClosed)) return null;
        var a = e.Args;
        var choices = Enumerable.Range(0, (int)Number(a, "pollChoiceCount"))
            .Select(i => new PollChoice((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Str(a, $"pollChoice{i}Title"),
                (long)Number(a, $"pollChoice{i}Votes")))
            .ToArray();
        var closed = e.Type == YouTubeEventTypes.PollClosed;
        var winner = !closed || choices.All(c => c.Votes == 0) ? -1 : Array.IndexOf(choices, choices.MaxBy(c => c.Votes));
        return new PollState("youtube", PollState.Poll, Str(a, "pollId"), Str(a, "pollTitle"), closed ? PollState.Ended : PollState.Active,
            choices, winner);
    }
}
