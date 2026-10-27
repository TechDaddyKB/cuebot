using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Starts, stops or toggles an OBS output: <see cref="Streaming"/> or <see cref="Recording"/>.</summary>
public sealed class OutputSubAction : ISubActionHandler
{
    private readonly Func<OutputCommand, CancellationToken, Task> _control;

    private OutputSubAction(string typeId, string displayName, Func<OutputCommand, CancellationToken, Task> control)
    {
        TypeId = typeId;
        DisplayName = displayName;
        _control = control;
    }

    public static OutputSubAction Streaming(ObsService obs) => new("obs.streaming", "Streaming", obs.ControlStreamAsync);

    public static OutputSubAction Recording(ObsService obs) => new("obs.recording", "Recording", obs.ControlRecordAsync);

    public string TypeId { get; }
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName { get; }
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("mode", "Mode", ParameterKind.Choice)
        {
            DefaultValue = nameof(OutputCommand.Start), Choices = ObsSubActionSupport.ChoicesOf<OutputCommand>(),
        },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await _control(ObsSubActionSupport.ParseChoice<OutputCommand>(p["mode"], "Mode"), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
