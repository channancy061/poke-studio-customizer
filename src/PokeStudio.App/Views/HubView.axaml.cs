using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PokeStudio.App.ViewModels;

namespace PokeStudio.App.Views;

public partial class HubView : UserControl
{
    public HubView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragOver(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void SetDragOver(bool value)
    {
        if (DataContext is HubViewModel vm)
        {
            vm.IsDragOver = value;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFile = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFile ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(hasFile);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
        var path = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
        if (path is not null && DataContext is HubViewModel vm)
        {
            await vm.OpenDroppedAsync(path);
        }
    }
}
