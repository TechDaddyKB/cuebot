namespace Cuebot.Core.Actions;

/// <summary>Contributes a family of trigger types (most modules build theirs from a table).</summary>
public interface ITriggerProvider
{
    IEnumerable<ITriggerType> GetTriggers();
}

/// <summary>All trigger and sub-action types contributed by the loaded modules.</summary>
public sealed class Registry
{
    private readonly Dictionary<string, ISubActionHandler> _subActions;
    private readonly Dictionary<string, ITriggerType> _triggers;

    public Registry(IEnumerable<ISubActionHandler> subActions, IEnumerable<ITriggerType> triggers)
        : this(subActions, triggers, [])
    {
    }

    public Registry(IEnumerable<ISubActionHandler> subActions, IEnumerable<ITriggerType> triggers,
        IEnumerable<ITriggerProvider> providers)
    {
        _subActions = subActions.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);
        _triggers = triggers.Concat(providers.SelectMany(p => p.GetTriggers()))
            .ToDictionary(t => t.TypeId, StringComparer.OrdinalIgnoreCase);
        SubActions = _subActions.Values.OrderBy(h => h.Category).ThenBy(h => h.DisplayName).ToArray();
        Triggers = _triggers.Values.OrderBy(t => t.Category).ThenBy(t => t.DisplayName).ToArray();
    }

    public IReadOnlyList<ISubActionHandler> SubActions { get; }
    public IReadOnlyList<ITriggerType> Triggers { get; }

    public ISubActionHandler? GetSubAction(string typeId) => _subActions.GetValueOrDefault(typeId);
    public ITriggerType? GetTrigger(string typeId) => _triggers.GetValueOrDefault(typeId);
}
