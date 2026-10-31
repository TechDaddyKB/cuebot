using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using static Cuebot.Obs.SubActions.ObsSubActionSupport;

namespace Cuebot.Obs.Streamlabs;

/// <summary>A Streamlabs Desktop sub-action built from a table entry in <see cref="StreamlabsCatalog"/>.</summary>
public sealed class StreamlabsSubAction(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => StreamlabsCatalog.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await run(context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Streamlabs Desktop sub-actions, triggers and script functions. Type ids are stored in actions.json.</summary>
public static class StreamlabsCatalog
{
    public const string Category = "Streamlabs Desktop";

    public static IReadOnlyList<ISubActionHandler> CreateSubActions(StreamlabsDesktopService slobs)
    {
        ParameterDescriptor Scene() => new("scene", "Scene") { Suggestions = () => slobs.SceneNames };
        ParameterDescriptor Source(string label = "Source") => new("source", label) { Suggestions = () => slobs.SourceNames };
        ParameterDescriptor Group() => new("group", "Group")
        {
            Suggestions = () => slobs.Scenes.SelectMany(s => s.Items).Where(i => i.IsFolder).Select(i => i.Name).Distinct().ToArray(),
        };
        ParameterDescriptor Choice<T>(string key, string label, T value) where T : struct, Enum =>
            new(key, label, ParameterKind.Choice) { DefaultValue = value.ToString(), Choices = ChoicesOf<T>() };
        StreamlabsSubAction Make(string id, string name, IReadOnlyList<ParameterDescriptor> parameters,
            Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new(id, name, parameters, run);

        return
        [
            Make("slobs.setScene", "Set active scene", [Scene()], (_, v, ct) => slobs.SetSceneAsync(Require(v["scene"], "Scene"), ct)),
            Make("slobs.getCurrentScene", "Get current scene", [new("resultArgument", "Save the answer in variable") { DefaultValue = "slobsScene" }],
                async (ctx, v, ct) =>
                {
                    var scene = await slobs.GetCurrentSceneAsync(ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], scene);
                    ctx.Args["currentScene"] = scene;   // Streamer.bot's name
                }),
            Make("slobs.setSourceVisibility", "Set source visibility state", [Scene(), Source(), Choice("state", "State", VisibilityMode.Toggle)],
                (_, v, ct) => slobs.SetSourceVisibilityAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<VisibilityMode>(v["state"], "State"), ct)),
            Make("slobs.hideSceneSources", "Hide all scene sources", [Scene()], (_, v, ct) => slobs.HideAllSourcesAsync(Require(v["scene"], "Scene"), ct)),
            Make("slobs.streaming", "Streaming", [Choice("mode", "Mode", OutputCommand.Start)],
                (_, v, ct) => slobs.ControlStreamingAsync(ParseChoice<OutputCommand>(v["mode"], "Mode"), ct)),
            Make("slobs.recording", "Recording", [Choice("mode", "Mode", OutputCommand.Start)],
                (_, v, ct) => slobs.ControlRecordingAsync(ParseChoice<OutputCommand>(v["mode"], "Mode"), ct)),
            Make("slobs.replayBuffer", "Replay buffer", [Choice("mode", "Mode", ReplayBufferCommand.Save)],
                (_, v, ct) => slobs.ControlReplayBufferAsync(ParseChoice<ReplayBufferCommand>(v["mode"], "Mode"), ct)),
            Make("slobs.setSourceMute", "Set source mute state", [Source(), Choice("state", "State", MuteMode.Toggle)],
                (_, v, ct) => slobs.SetSourceMuteAsync(Require(v["source"], "Source"), ParseChoice<MuteMode>(v["state"], "State"), ct)),
            Make("slobs.setText", "Set text", [Source("Text source"), new("text", "Text", ParameterKind.Multiline)],
                (_, v, ct) => slobs.UpdateSourceSettingsAsync(Require(v["source"], "Text source"), new { text = v["text"] }, ct)),
            Make("slobs.setBrowserUrl", "Set browser source URL", [Source("Browser source"), new("url", "URL")],
                (_, v, ct) => slobs.UpdateSourceSettingsAsync(Require(v["source"], "Browser source"), new { url = v["url"] }, ct)),
            Make("slobs.setFilterState", "Set source filter state",
                [Source("Source or scene"), new("filter", "Filter"),
                 new("state", "State", ParameterKind.Choice) { DefaultValue = "Enable", Choices = ["Enable", "Disable"] }],
                (_, v, ct) => slobs.SetFilterStateAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"),
                    ParseChoice<EnableMode>(v["state"], "State") == EnableMode.Enable, ct)),
            Make("slobs.setSceneFilterState", "Set scene filter state",
                [Scene(), new("filter", "Filter"), Choice("state", "State", VisibilityMode.Toggle)],
                (_, v, ct) => slobs.SetSceneFilterStateAsync(Require(v["scene"], "Scene"), Require(v["filter"], "Filter"),
                    ParseChoice<VisibilityMode>(v["state"], "State"), ct)),
            Make("slobs.setRandomFilterState", "Set state of a random filter",
                [Scene(), Source("Source (empty for the scene's own filters)"), Choice("state", "State", VisibilityMode.Visible),
                 new("resultArgument", "Save the filter name in variable") { DefaultValue = "slobsFilterName" }],
                async (ctx, v, ct) => Store(ctx, v["resultArgument"], await slobs.SetRandomFilterStateAsync(Require(v["scene"], "Scene"),
                    v["source"], ParseChoice<VisibilityMode>(v["state"], "State"), ct).ConfigureAwait(false))),
            Make("slobs.hideSourceFilters", "Hide source's filters", [Scene(), Source()],
                (_, v, ct) => slobs.HideSourceFiltersAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"), ct)),
            Make("slobs.hideGroupSources", "Hide group's sources", [Scene(), Group()],
                (_, v, ct) => slobs.HideGroupSourcesAsync(Require(v["scene"], "Scene"), Require(v["group"], "Group"), ct)),
            Make("slobs.randomGroupSource", "Set random group source visible",
                [Scene(), Group(), new("resultArgument", "Save the source name in variable") { DefaultValue = "randomSource" }],
                async (ctx, v, ct) => Store(ctx, v["resultArgument"], await slobs.SetRandomGroupSourceVisibleAsync(
                    Require(v["scene"], "Scene"), Require(v["group"], "Group"), ct).ConfigureAwait(false))),
            Make("slobs.flipSource", "Flip source", [Scene(), Source(), Choice("axis", "Axis", FlipAxis.Horizontal)],
                (_, v, ct) => slobs.FlipSourceAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<FlipAxis>(v["axis"], "Axis"), ct)),
            Make("slobs.rotateSource", "Rotate source", [Scene(), Source(), new("degrees", "Degrees") { DefaultValue = "90" }],
                (_, v, ct) => slobs.RotateSourceAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    RequireNumber(v["degrees"], "Degrees"), ct)),
            Make("slobs.raw", "Send Streamlabs command (advanced)",
                [new("resource", "Resource") { DefaultValue = "ScenesService" }, new("method", "Method") { DefaultValue = "getScenes" },
                 new("args", "Arguments (JSON array)", ParameterKind.Multiline) { Help = "Leave empty for none" },
                 new("resultArgument", "Save the answer in variable") { DefaultValue = "slobsResult" }],
                async (ctx, v, ct) =>
                {
                    var args = ParseJson(v["args"], "Arguments", JsonValueKind.Array) is { } array
                        ? array.EnumerateArray().Select(a => (object?)a).ToArray()
                        : null;
                    var result = await slobs.CallAsync(Require(v["resource"], "Resource"), Require(v["method"], "Method"), args, ct)
                        .ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], result.GetRawText());
                }),
        ];
    }

    public static IEnumerable<ITriggerType> CreateTriggers(StreamlabsDesktopService slobs) =>
    [
        new EventTriggerType(StreamlabsEvents.SceneChanged, Category, "Scene changed", StreamlabsEvents.SceneChanged,
            [new ParameterDescriptor("scene", "Scene") { Suggestions = () => slobs.SceneNames, Help = "Leave empty for any scene" }],
            (e, settings, args) => ObsTriggers.MatchesSceneFilter(e, settings, args)),
        new EventTriggerType(StreamlabsEvents.StreamStarted, Category, "Streaming started", StreamlabsEvents.StreamStarted),
        new EventTriggerType(StreamlabsEvents.StreamStopped, Category, "Streaming stopped", StreamlabsEvents.StreamStopped),
        new EventTriggerType(StreamlabsEvents.RecordStarted, Category, "Recording started", StreamlabsEvents.RecordStarted),
        new EventTriggerType(StreamlabsEvents.RecordStopped, Category, "Recording stopped", StreamlabsEvents.RecordStopped),
        new EventTriggerType(StreamlabsEvents.ReplayBufferSaved, Category, "Replay buffer saved", StreamlabsEvents.ReplayBufferSaved),
        new EventTriggerType(StreamlabsEvents.Connected, Category, "Connected", StreamlabsEvents.Connected),
        new EventTriggerType(StreamlabsEvents.Disconnected, Category, "Disconnected", StreamlabsEvents.Disconnected),
    ];

    public static IEnumerable<ScriptFunction> CreateScriptFunctions(StreamlabsDesktopService slobs) =>
    [
        new("slobs.call", "Call a Streamlabs Desktop Remote Control API method; returns its result", ["resource", "method", "args"],
            async (_, args, ct) =>
            {
                var resource = args.GetString("resource");
                var method = args.GetString("method");
                if (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(method))
                    throw new ScriptException("slobs.call needs a resource and a method");
                var callArgs = args.GetElement("args") is { ValueKind: JsonValueKind.Array } array
                    ? array.EnumerateArray().Select(a => (object?)a).ToArray()
                    : null;
                return await slobs.CallAsync(resource, method, callArgs, ct).ConfigureAwait(false);
            }),
    ];
}

/// <summary>Registers the Streamlabs Desktop triggers.</summary>
public sealed class StreamlabsTriggers(StreamlabsDesktopService slobs) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() => StreamlabsCatalog.CreateTriggers(slobs);
}

/// <summary>Registers the Streamlabs Desktop script functions.</summary>
public sealed class StreamlabsScriptFunctions(StreamlabsDesktopService slobs) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() => StreamlabsCatalog.CreateScriptFunctions(slobs);
}
