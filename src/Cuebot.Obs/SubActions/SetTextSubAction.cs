using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>Replaces the text of a text source.</summary>
public sealed class SetTextSubAction(ObsService obs) : ISubActionHandler
{
    public string TypeId => "obs.setText";
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName => "Set text";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("input", "Text source") { Suggestions = () => obs.Inputs },
        new("text", "Text", ParameterKind.Multiline),
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await obs.SetInputTextAsync(ObsSubActionSupport.Require(p["input"], "Text source"), p["text"], ct)
            .ConfigureAwait(false);
        return SubActionResult.Next;
    }
}
