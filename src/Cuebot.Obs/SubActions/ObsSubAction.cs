using Cuebot.Core.Actions;

namespace Cuebot.Obs.SubActions;

/// <summary>
/// An OBS sub-action built from a table entry (see <see cref="ObsSubActionCatalog"/>). Every OBS sub-action starts
/// with a "Connection" parameter; empty means the default connection.
/// </summary>
public sealed class ObsSubAction : ISubActionHandler
{
    public const string ConnectionKey = "connection";

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
        await _run(_obs.Get(p[ConnectionKey]), context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }

    internal static ParameterDescriptor ConnectionParameter(ObsService obs) => new(ConnectionKey, "Connection")
    {
        Suggestions = () => obs.ConnectionNames,
        Help = "OBS connection name; leave empty for the default connection",
    };
}
