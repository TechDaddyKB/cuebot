using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Shows, hides or toggles a source inside a scene.</summary>
public sealed class SetSourceVisibilitySubAction(ObsService obs) : ISubActionHandler
{
    public string TypeId => "obs.setSourceVisibility";
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName => "Set source visibility";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("scene", "Scene") { Suggestions = () => obs.Scenes },
        new("source", "Source") { Suggestions = () => obs.Inputs.Concat(obs.Scenes).Distinct() },
        new("state", "State", ParameterKind.Choice)
        {
            DefaultValue = nameof(VisibilityMode.Toggle), Choices = ObsSubActionSupport.ChoicesOf<VisibilityMode>(),
        },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await obs.SetSourceVisibilityAsync(
            ObsSubActionSupport.Require(p["scene"], "Scene"),
            ObsSubActionSupport.Require(p["source"], "Source"),
            ObsSubActionSupport.ParseChoice<VisibilityMode>(p["state"], "State"),
            ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
