using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Switches the OBS program scene.</summary>
public sealed class SetSceneSubAction(ObsService obs) : ISubActionHandler
{
    public string TypeId => "obs.setScene";
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName => "Set scene";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("scene", "Scene") { Suggestions = () => obs.Scenes }];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await obs.SetCurrentSceneAsync(ObsSubActionSupport.Require(p["scene"], "Scene"), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
