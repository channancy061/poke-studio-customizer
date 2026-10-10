using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PokeStudio.App.ViewModels;

namespace PokeStudio.App.Views;

public partial class ShellView : UserControl
{
    private GlobalSearchViewModel? _search;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_search is not null)
            {
                _search.PropertyChanged -= OnSearchChanged;
            }

            _search = (DataContext as ShellViewModel)?.Search;
            if (_search is not null)
            {
                _search.PropertyChanged += OnSearchChanged;
            }
        };
    }

    private void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobalSearchViewModel.IsOpen) && _search?.IsOpen == true)
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_search is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                _search.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                _search.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _search.Activate();
                e.Handled = true;
                break;
            case Key.Escape:
                _search.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultActivated(object? sender, TappedEventArgs e) => _search?.Activate();

    private void OnSearchScrimPressed(object? sender, PointerPressedEventArgs e) => _search?.Close();

    // clicks inside the search box mustn't reach the scrim behind it
    private void OnSearchCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
