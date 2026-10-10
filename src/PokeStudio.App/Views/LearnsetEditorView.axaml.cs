using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PokeStudio.App.ViewModels;

namespace PokeStudio.App.Views;

public partial class LearnsetEditorView : UserControl
{
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateStringApplicationFormat("pokestudio-levelup-row");
    private Point? _pressedAt;
    private LevelUpRowViewModel? _pressedRow;

    public LearnsetEditorView()
    {
        InitializeComponent();
        LevelUpList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        LevelUpList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: LevelUpRowViewModel row } c)
        {
            _pressedAt = e.GetPosition(c);
            _pressedRow = row;
        }
    }

    private async void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedRow is not { } row || _pressedAt is not { } start || sender is not Control c || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(c) - start;
        if (Math.Abs(delta.Y) < 4 && Math.Abs(delta.X) < 4)
        {
            return;
        }

        _pressedAt = null;
        _pressedRow = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RowFormat, row.Index.ToString(CultureInfo.InvariantCulture)));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(RowFormat) ? DragDropEffects.Move : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LearnsetEditorViewModel vm
            || e.DataTransfer.TryGetValue(RowFormat) is not { } text
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
        {
            return;
        }

        var target = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(v => v.DataContext).OfType<LevelUpRowViewModel>().FirstOrDefault();
        var to = target?.Index ?? vm.LevelUp.Count - 1;
        vm.MoveRowTo(from, to);
    }
}
