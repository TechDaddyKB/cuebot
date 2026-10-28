using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Transfer.StreamerBot;

namespace Cuebot.Tests.Variables;

/// <summary>
/// The run arguments Streamer.bot 1.0.7 puts on every run (<c>__source</c>, <c>eventSource</c>, <c>triggerName</c>,
/// <c>triggerCategory</c>, <c>triggerId</c>, <c>actionId</c>, <c>actionName</c>, <c>actionQueuedAt</c>,
/// <c>runningActionId</c>, <c>queueName</c>, <c>isTest</c>), with its types, on every Cuebot trigger that imports from a
/// Streamer.bot trigger. Expected values come from Streamer.bot's own Test Trigger runs.
/// </summary>
public sealed class StreamerBotRunVariablesTests : IDisposable
{
    private readonly AppLog _log = new();

    /// <summary>Every Streamer.bot code with a Cuebot trigger: (code, Cuebot trigger type).</summary>
    private static IEnumerable<(int Code, string Target)> MappedCodes() => StreamerBotMappingTable.Default.Triggers
        .SelectMany(r => r.TypeKeys.Select(k => (Ok: int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var n), Code: n, r.TargetType)))
        .Where(x => x.Ok && StreamerBotTriggerCatalog.Paths.ContainsKey(x.Code))
        .Select(x => (x.Code, x.TargetType))
        .Distinct();

    /// <summary>Event arguments that tell apart the Streamer.bot triggers sharing one Cuebot trigger.</summary>
    private static Dictionary<string, object?> EventArgsFor(int code) => code switch
    {
        120 or 124 => new() { ["platform"] = "Twitch" },
        4016 or 4017 => new() { ["platform"] = "YouTube" },
        35008 => new() { ["platform"] = "Kick" },
        180 or 181 => new() { ["platform"] = "twitch", ["provider"] = "7TV" },
        182 or 183 => new() { ["platform"] = "twitch", ["provider"] = "BTTV" },
        _ => [],
    };

    [Fact]
    public async Task Every_mapped_trigger_run_carries_streamerbots_run_arguments_with_its_types()
    {
        var mapped = MappedCodes().ToList();
        Assert.Equal(371, mapped.Select(m => m.Code).Distinct().Count());   // docs/streamerbot-import-codes.md

        var profile = new ProfileService(null, _log);
        var targets = mapped.Select(m => m.Target).Distinct(StringComparer.Ordinal).ToList();
        var registry = new Registry([], targets.Select(t => new EventTriggerType(t, "Test", "Cuebot " + t, t)));
        using var engine = new ActionEngine(new EventBus(_log), profile, registry, new GlobalVariables(null, _log), _log);
        var runs = new ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>>();
        engine.RunCompleted += r => runs[r.ActionName] = r.Arguments!;
        var triggers = new Dictionary<string, TriggerDefinition>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var trigger = new TriggerDefinition { Type = target };
            triggers[target] = trigger;
            profile.Profile.Actions.Add(new ActionDefinition { Name = target, Triggers = [trigger] });
        }
        profile.NotifyChanged();

        foreach (var (code, target) in mapped)
        {
            runs.Clear();
            await Task.WhenAll(engine.Dispatch(new BotEvent(target, EventArgsFor(code))));
            var args = runs[target];
            var path = StreamerBotTriggerCatalog.Paths[code].Split(" > ");
            var where = $"{code} ({target})";

            Assert.True(args["__source"] is int source && source == code, $"{where}: __source {args["__source"]}");
            Assert.Equal(path[^1], args["triggerName"]);
            Assert.Equal(string.Join('/', path[..^1]), args["triggerCategory"]);
            Assert.IsType<string>(args["eventSource"]);
            Assert.Equal(triggers[target].Id, args["triggerId"]);
            Assert.Equal(profile.Snapshot.FindAction(target)!.Id, args["actionId"]);
            Assert.Equal(target, args["actionName"]);
            Assert.IsType<DateTime>(args["actionQueuedAt"]);
            Assert.IsType<Guid>(args["runningActionId"]);
            Assert.Equal("Default", args["queueName"]);
            Assert.Equal(false, args["isTest"]);
            // Cuebot's own names stay.
            Assert.Equal(target, args["triggerType"]);
            Assert.Equal(target, args["eventType"]);
            Assert.Equal(args["runningActionId"]!.ToString(), args["runId"]);
        }
    }

    [Theory]
    // eventSource as Streamer.bot's Test Trigger runs report it.
    [InlineData(706, "misc", "Core", "Streamer.bot Started")]
    [InlineData(701, "misc", "Core", "Timed Actions")]
    [InlineData(511, "filetail", "Core/File IO/File Tail", "Changed")]
    [InlineData(501, "filewatcher", "Core/File/Folder Watcher", "Changed")]
    [InlineData(711, "misc", "Core/Global Variables", "Global Variable Created")]
    [InlineData(32001, "group", "Core/Groups", "Added")]
    [InlineData(16001, "inputs", "Core/Inputs", "Key Press")]
    [InlineData(15001, "midi", "Core/MIDI", "MIDI Message")]
    [InlineData(703, "misc", "Core/Processes", "Process Started")]
    [InlineData(708, "misc", "Core/System", "Toast Activation")]
    [InlineData(18001, "custom", "Custom", "Custom")]
    [InlineData(21301, "elgato", "Elgato/Camera Hub", "Connected")]
    [InlineData(17004, "streamdeck", "Elgato/Stream Deck", "Stream Deck Info")]
    [InlineData(21001, "elgato", "Elgato/Wave Link", "Output Switched")]
    [InlineData(6001, "hyperate", "Integrations/HypeRate.io", "Heart Rate Pulse")]
    [InlineData(28003, "pallygg", "Integrations/Pally.gg", "Campaign Tip")]
    [InlineData(1201, "streamelements", "Integrations/StreamElements", "Tip")]
    [InlineData(35021, "kick", "Kick/Emotes", "SevenTV Emote Added")]
    [InlineData(14004, "obs", "OBS Studio", "Scene Changed")]
    [InlineData(22001, "streamlabsdesktop", "Streamlabs Desktop", "Connected")]
    [InlineData(101, "twitch", "Twitch/Channel", "Follow")]
    [InlineData(452, "twitch", "Twitch/Chat/Mode", "Emote Mode Off")]
    [InlineData(4016, "youtube", "YouTube/Chat", "First Words")]
    public void Catalog_reports_what_streamerbot_runs_report(int code, string eventSource, string category, string name) =>
        Assert.Equal(new StreamerBotTrigger(code, category, name, eventSource), StreamerBotTriggerCatalog.Get(code));

    [Fact]
    public void Catalog_lists_every_streamerbot_trigger_and_tells_shared_cuebot_triggers_apart()
    {
        Assert.Equal(419, StreamerBotTriggerCatalog.Paths.Count);
        Assert.All(StreamerBotMappingTable.NoEquivalentTriggers.Keys, c => Assert.True(StreamerBotTriggerCatalog.Paths.ContainsKey(int.Parse(c, CultureInfo.InvariantCulture)), c));
        Assert.Null(StreamerBotTriggerCatalog.Get(99999));

        Assert.Equal(35008, StreamerBotTriggerCatalog.For("users.firstWords", new Dictionary<string, object?> { ["platform"] = "Kick" })?.Code);
        Assert.Null(StreamerBotTriggerCatalog.For("users.firstWords", new Dictionary<string, object?> { ["platform"] = "Rumble" }));
        // A test run has no event: the trigger's own settings decide.
        Assert.Equal(4016, StreamerBotTriggerCatalog.For("users.firstWords", new Dictionary<string, object?>(),
            new Dictionary<string, string> { ["platform"] = "YouTube" })?.Code);
        Assert.Equal(183, StreamerBotTriggerCatalog.For("chat.emoteRemoved", new Dictionary<string, object?> { ["platform"] = "twitch", ["provider"] = "BTTV" })?.Code);
        Assert.Null(StreamerBotTriggerCatalog.For("chat.emoteAdded", new Dictionary<string, object?> { ["platform"] = "twitch", ["provider"] = "FFZ" }));
        Assert.Equal(18001, StreamerBotTriggerCatalog.For("custom.codeEvent", new Dictionary<string, object?>())?.Code);
    }

    [Fact]
    public async Task Cuebot_only_triggers_test_runs_and_manual_runs_have_the_run_arguments_too()
    {
        var profile = new ProfileService(null, _log);
        var registry = new Registry([], [new EventTriggerType("rumble.follow", "Rumble", "Follow (Rumble)", "rumble.follow"),
            new EventTriggerType("twitch.follow", "Twitch", "Follow", "twitch.follow")]);
        using var engine = new ActionEngine(new EventBus(_log), profile, registry, new GlobalVariables(null, _log), _log);
        IReadOnlyDictionary<string, object?>? last = null;
        engine.RunCompleted += r => last = r.Arguments;
        var trigger = new TriggerDefinition { Type = "rumble.follow" };
        var action = new ActionDefinition { Name = "Thanks", Queue = "Alerts", Triggers = [trigger, new TriggerDefinition { Type = "twitch.follow" }] };
        profile.Profile.Actions.Add(action);
        profile.NotifyChanged();

        await Task.WhenAll(engine.Dispatch(new BotEvent("rumble.follow")));
        Assert.Equal(0, last!["__source"]);   // EventType.None: Streamer.bot has no such trigger
        Assert.Equal("Follow (Rumble)", last["triggerName"]);
        Assert.Equal("Rumble", last["triggerCategory"]);
        Assert.Equal("rumble", last["eventSource"]);
        Assert.Equal(trigger.Id, last["triggerId"]);
        Assert.Equal("Alerts", last["queueName"]);

        Assert.True(await engine.TestTriggerAsync(action, action.Triggers[1]));
        Assert.Equal((101, "Follow", "Twitch/Channel", "twitch", true), (last["__source"], last["triggerName"], last["triggerCategory"], last["eventSource"], last["isTest"]));

        Assert.True(await engine.RunAsync(action));
        Assert.Equal((false, "Alerts", action.Id), (last["isTest"], last["queueName"], last["actionId"]));
        Assert.IsType<Guid>(last["runningActionId"]);
        Assert.IsType<DateTime>(last["actionQueuedAt"]);
        Assert.False(last.ContainsKey("__source"));   // not started by a trigger
    }

    public void Dispose() => _log.Dispose();
}
