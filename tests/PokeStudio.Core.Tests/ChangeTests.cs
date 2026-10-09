using PokeStudio.Core.Changes;

namespace PokeStudio.Core.Tests;

public class ChangeTests
{
    private sealed class Box
    {
        public int Value { get; set; }
    }

    private static ValueChange<int> Set(Box box, int to) =>
        new("Creatures", $"Speed {box.Value} → {to}", v => box.Value = v, box.Value, to);

    [Fact]
    public void Undo_and_redo_restore_values_in_order()
    {
        var box = new Box { Value = 45 };
        var stack = new UndoRedoStack();

        stack.Execute(Set(box, 60));
        stack.Execute(Set(box, 90));
        Assert.Equal(90, box.Value);

        stack.Undo();
        Assert.Equal(60, box.Value);
        stack.Undo();
        Assert.Equal(45, box.Value);
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.Equal(60, box.Value);
    }

    [Fact]
    public void New_change_clears_redo()
    {
        var box = new Box();
        var stack = new UndoRedoStack();
        stack.Execute(Set(box, 1));
        stack.Undo();
        stack.Execute(Set(box, 2));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Composite_changes_undo_as_one_in_reverse_order()
    {
        var log = new List<string>();
        var a = new ValueChange<string>("x", "a", v => log.Add("a:" + v), "old", "new");
        var b = new ValueChange<string>("x", "b", v => log.Add("b:" + v), "old", "new");
        var stack = new UndoRedoStack();

        stack.Execute(new CompositeChange("x", "both", [a, b]));
        stack.Undo();

        Assert.Equal(["a:new", "b:new", "b:old", "a:old"], log);
    }

    [Fact]
    public void Unsaved_count_tracks_the_save_point()
    {
        var box = new Box();
        var stack = new UndoRedoStack();
        stack.Execute(Set(box, 1));
        stack.Execute(Set(box, 2));
        Assert.Equal(2, stack.UnsavedCount);

        stack.MarkSaved();
        Assert.False(stack.IsDirty);

        stack.Undo();
        Assert.Equal(1, stack.UnsavedCount); // undoing past the save point is a change too
        stack.Redo();
        Assert.False(stack.IsDirty);

        stack.Undo();
        stack.Execute(Set(box, 5)); // saved state is now unreachable
        Assert.True(stack.IsDirty);
    }

    [Fact]
    public void Single_change_can_be_undone_without_losing_later_ones()
    {
        var speed = new Box { Value = 45 };
        var hp = new Box { Value = 50 };
        var stack = new UndoRedoStack();
        var first = Set(speed, 90);
        stack.Execute(first);
        stack.Execute(Set(hp, 70));

        Assert.True(stack.UndoSingle(first));
        Assert.Equal(45, speed.Value);
        Assert.Equal(70, hp.Value);
        Assert.Single(stack.History);
    }

    [Fact]
    public void Change_log_persists_actions_to_sqlite()
    {
        using var tmp = new TempDir();
        var db = tmp.Combine("history.db");
        var box = new Box();
        var stack = new UndoRedoStack();

        using (var log = new ChangeLog(db))
        {
            log.Attach(stack);
            stack.Execute(Set(box, 90));
            stack.Undo();
            log.MarkSaved();
        }

        using var reopened = new ChangeLog(db);
        var entries = reopened.ReadRecent();
        Assert.Equal(2, entries.Count);
        Assert.Equal(ChangeAction.Undone, entries[0].Action);
        Assert.Equal("Speed 0 → 90", entries[1].Description);
        Assert.All(entries, e => Assert.True(e.Saved));
    }
}
