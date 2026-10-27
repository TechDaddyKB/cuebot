using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.HeartRate;

/// <summary>"Get heart rate" (category "Heart Rate"): the latest BPM of Pulsoid, HypeRate or whichever reported last.</summary>
internal static class HeartRateSubActions
{
    public const string Category = "Heart Rate";

    public static IReadOnlyList<ISubActionHandler> Create(HeartRateHub hub, TimeProvider clock) =>
    [
        new IntegrationSubAction("heartrate.get", Category, "Get heart rate",
            [new("source", "Source", ParameterKind.Choice) { DefaultValue = "Latest", Choices = ["Latest", HeartRateHub.PulsoidSource, HeartRateHub.HypeRateSource] }],
            (ctx, v, _) =>
            {
                var reading = hub.Get(v["source"]);
                ctx.Args["heartRateAvailable"] = reading is not null;
                ctx.Args["heartRate"] = reading?.Bpm ?? 0;
                ctx.Args["heartRateSource"] = reading?.Source ?? "";
                ctx.Args["heartRateTime"] = reading?.Time.ToString("o") ?? "";
                ctx.Args["heartRateAgeSeconds"] = reading is null ? -1 : Math.Round((clock.GetUtcNow() - reading.Time).TotalSeconds, 1);
                return Task.CompletedTask;
            }),
    ];
}
