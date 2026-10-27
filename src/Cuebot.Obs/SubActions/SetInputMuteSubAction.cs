using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Mutes, unmutes or toggles an audio input.</summary>
public sealed class SetInputMuteSubAction(ObsService obs) : ISubActionHandler
{
    public string TypeId => "obs.setInputMute";
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName => "Set input mute";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("input", "Input") { Suggestions = () => obs.Inputs },
        new("state", "State", ParameterKind.Choice)
        {
            DefaultValue = nameof(MuteMode.Toggle), Choices = ObsSubActionSupport.ChoicesOf<MuteMode>(),
        },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await obs.SetInputMuteAsync(
            ObsSubActionSupport.Require(p["input"], "Input"),
            ObsSubActionSupport.ParseChoice<MuteMode>(p["state"], "State"),
            ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
