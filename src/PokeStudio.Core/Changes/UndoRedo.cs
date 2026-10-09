namespace PokeStudio.Core.Changes;

/// <summary>One reversible edit with a plain-English description ("Leafling · Speed 45 → 90").</summary>
public interface IChange
{
    string Description { get; }

    /// <summary>Which editor area the change belongs to (e.g. "Pokémon"), for grouping in the Changes panel.</summary>
    string Area { get; }

    void Apply();
    void Revert();
}

/// <summary>A change that can absorb a following change to the same thing (e.g. one slider drag = one undo step).</summary>
public interface IMergeableChange : IChange
{
    /// <summary>Changes with the same non-null key may merge, e.g. "species:1:speed".</summary>
    string? MergeKey { get; }

    IChange MergeWith(IChange next);
}

/// <summary>Sets a value through a setter; undo restores the old value.</summary>
public sealed class ValueChange<T>(
    string area,
    string description,
    Action<T> setter,
    T oldValue,
    T newValue,
    string? mergeKey = null,
    Func<T, T, string>? describe = null) : IMergeableChange
{
    public string Area { get; } = area;
    public string Description { get; } = description;
    public T OldValue { get; } = oldValue;
    public T NewValue { get; } = newValue;
    public string? MergeKey { get; } = mergeKey;

    public void Apply() => setter(NewValue);
    public void Revert() => setter(OldValue);

    public IChange MergeWith(IChange next)
    {
        var n = (ValueChange<T>)next;
        var text = describe?.Invoke(OldValue, n.NewValue) ?? n.Description;
        return new ValueChange<T>(Area, text, setter, OldValue, n.NewValue, MergeKey, describe);
    }
}

/// <summary>Several changes undone and redone as one (e.g. "Apply randomizer").</summary>
public sealed class CompositeChange(string area, string description, IReadOnlyList<IChange> parts) : IChange
{
    public string Area { get; } = area;
    public string Description { get; } = description;
    public IReadOnlyList<IChange> Parts { get; } = parts;

    public void Apply()
    {
        foreach (var part in Parts)
        {
            part.Apply();
        }
    }

    public void Revert()
    {
        for (var i = Parts.Count - 1; i >= 0; i--)
        {
            Parts[i].Revert();
        }
    }
}

public enum ChangeAction
{
    Applied,
    Undone,
    Redone,
    /// <summary>The newest change absorbed a follow-up edit (description updated).</summary>
    Merged,
}

public sealed class ChangeEventArgs(IChange change, ChangeAction action) : EventArgs
{
    public IChange Change { get; } = change;
    public ChangeAction Action { get; } = action;
}

/// <summary>Undo/redo stack with a save point so the UI can show "3 unsaved changes".</summary>
public sealed class UndoRedoStack(TimeProvider? clock = null)
{
    /// <summary>Edits to the same thing closer together than this become one undo step.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1.5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<IChange> _undo = [];
    private readonly Stack<IChange> _redo = new();
    private int _savePoint;
    private DateTimeOffset _lastExecute = DateTimeOffset.MinValue;

    public event EventHandler<ChangeEventArgs>? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<IChange> History => _undo;
    public IChange? NextUndo => CanUndo ? _undo[^1] : null;
    public IChange? NextRedo => CanRedo ? _redo.Peek() : null;

    /// <summary>How far the current state is from the last save (undoing past the save point counts too).
    /// If the saved state became unreachable, the project is always dirty.</summary>
    public int UnsavedCount => _savePoint < 0 ? Math.Max(1, _undo.Count) : Math.Abs(_undo.Count - _savePoint);

    public bool IsDirty => UnsavedCount != 0;

    /// <summary>Applies a change and records it. Clears the redo stack.</summary>
    public void Execute(IChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Apply();
        var now = _clock.GetUtcNow();
        var canMerge = _undo.Count > 0
            && _savePoint != _undo.Count
            && now - _lastExecute <= MergeWindow
            && change is IMergeableChange { MergeKey: not null } incoming
            && _undo[^1] is IMergeableChange { MergeKey: not null } top
            && top.MergeKey == incoming.MergeKey
            && top.GetType() == change.GetType();
        _lastExecute = now;
        if (canMerge)
        {
            var merged = ((IMergeableChange)_undo[^1]).MergeWith(change);
            _undo[^1] = merged;
            _redo.Clear();
            Changed?.Invoke(this, new ChangeEventArgs(merged, ChangeAction.Merged));
            return;
        }

        if (_savePoint > _undo.Count)
        {
            _savePoint = -1; // the saved state can no longer be reached by undo/redo
        }

        _undo.Add(change);
        _redo.Clear();
        Changed?.Invoke(this, new ChangeEventArgs(change, ChangeAction.Applied));
    }

    public IChange? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        var change = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        change.Revert();
        _redo.Push(change);
        Changed?.Invoke(this, new ChangeEventArgs(change, ChangeAction.Undone));
        return change;
    }

    public IChange? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        var change = _redo.Pop();
        change.Apply();
        _undo.Add(change);
        Changed?.Invoke(this, new ChangeEventArgs(change, ChangeAction.Redone));
        return change;
    }

    /// <summary>Undo one specific change from the history without losing later ones.
    /// Only safe for independent changes; returns false if the change isn't in the history.</summary>
    public bool UndoSingle(IChange change)
    {
        var index = _undo.IndexOf(change);
        if (index < 0)
        {
            return false;
        }

        change.Revert();
        _undo.RemoveAt(index);
        if (_savePoint > index)
        {
            _savePoint = -1;
        }

        Changed?.Invoke(this, new ChangeEventArgs(change, ChangeAction.Undone));
        return true;
    }

    public void MarkSaved() => _savePoint = _undo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _savePoint = 0;
    }
}
