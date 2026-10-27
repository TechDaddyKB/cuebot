using System.Collections.ObjectModel;

namespace Cuebot.App.ViewModels;

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> hold exactly <paramref name="desired"/> (by reference, in order) with removes,
    /// moves and inserts only, so items that stay keep their selection and visuals.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (!ReferenceEquals(target[j], desired[i])) continue;
                existing = j;
                break;
            }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
    }
}

/// <summary>A name and formatted value, e.g. one argument of an action run.</summary>
public sealed record NameValueRow(string Name, string Value);
