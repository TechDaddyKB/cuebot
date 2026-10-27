namespace Cuebot.App.ViewModels.Actions;

/// <summary>
/// Undo/redo for one action: a bounded stack of JSON snapshots of the action taken after each edit. Rapid edits with
/// the same coalesce key (typing in one field, spinning one weight) inside <see cref="CoalesceWindow"/> become one step.
/// </summary>
public sealed class EditHistory(string initial)
{
    public const int MaxDepth = 100;

    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string? _lastKey;
    private DateTime _lastEdit;

    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The action as it is now.</summary>
    public string Current { get; private set; } = initial;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>Records the state after an edit. Returns false when nothing changed.</summary>
    public bool Record(string state, string? coalesceKey, DateTime now)
    {
        if (state == Current) return false;
        var coalesce = coalesceKey is not null && coalesceKey == _lastKey && _undo.Count > 0 && now - _lastEdit <= CoalesceWindow;
        if (!coalesce)
        {
            _undo.AddLast(Current);
            if (_undo.Count > MaxDepth) _undo.RemoveFirst();
        }
        Current = state;
        _redo.Clear();
        _lastKey = coalesceKey;
        _lastEdit = now;
        return true;
    }

    /// <summary>
    /// Adopts a change made outside the editor (e.g. "Set action state", a queue rename) without making it an undo
    /// step. With <paramref name="patchStored"/>, every stored snapshot is rewritten too, so undo and redo keep the
    /// outside change instead of reverting it.
    /// </summary>
    public void Rebase(string state, Func<string, string>? patchStored = null)
    {
        Current = state;
        _lastKey = null;
        if (patchStored is null) return;
        for (var node = _undo.First; node is not null; node = node.Next) node.Value = patchStored(node.Value);
        var redo = _redo.Reverse().Select(patchStored).ToArray();
        _redo.Clear();
        foreach (var s in redo) _redo.Push(s);
    }

    /// <summary>Returns the state to restore, or null when there is nothing to undo.</summary>
    public string? Undo()
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(Current);
        Current = last.Value;
        _lastKey = null;
        return Current;
    }

    public string? Redo()
    {
        if (!_redo.TryPop(out var next)) return null;
        _undo.AddLast(Current);
        Current = next;
        _lastKey = null;
        return Current;
    }
}
