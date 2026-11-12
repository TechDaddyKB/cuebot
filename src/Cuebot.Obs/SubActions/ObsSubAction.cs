using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>
/// An OBS sub-action built from a table entry (see <see cref="ObsSubActionCatalog"/>). Every OBS sub-action starts
/// with a "Connection" parameter; empty means the default connection.
/// </summary>
public sealed class ObsSubAction : ISubActionHandler
{
    public const string ConnectionKey = "connection";

    /// <summary>Optional parameter: run the step's requests against another canvas (OBS 32.1+).</summary>
    public const string CanvasKey = "canvas";

    private readonly ObsService _obs;
    private readonly Func<ObsConnection, ActionRunContext, ParameterValues, CancellationToken, Task> _run;

    internal ObsSubAction(ObsService obs, string typeId, string displayName, IEnumerable<ParameterDescriptor> parameters,
        Func<ObsConnection, ActionRunContext, ParameterValues, CancellationToken, Task> run)
    {
        _obs = obs;
        _run = run;
        TypeId = typeId;
        DisplayName = displayName;
        Parameters = [ConnectionParameter(obs), .. parameters];
    }

    public string TypeId { get; }
    public string Category => ObsSubActionSupport.Category;
    public string DisplayName { get; }
    public IReadOnlyList<ParameterDescriptor> Parameters { get; }

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var connection = _obs.Get(p[ConnectionKey]);
        using (connection.UseCanvas(p[CanvasKey]))
            await _run(connection, context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }

    internal static ParameterDescriptor ConnectionParameter(ObsService obs) => new(ConnectionKey, "Connection")
    {
        Suggestions = () => obs.ConnectionNames,
        Help = "OBS connection name; leave empty for the default connection",
    };
}
