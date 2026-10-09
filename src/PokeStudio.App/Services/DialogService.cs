using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PokeStudio.App.Views;

namespace PokeStudio.App.Services;

public interface IDialogService
{
    Task<string?> PickGameFileAsync();
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);
}

/// <summary>File pickers use Avalonia's storage provider (native dialogs on Windows, portals on Linux).</summary>
public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    public async Task<string?> PickGameFileAsync()
    {
        if (Owner is null)
        {
            return null;
        }

        var files = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a game you own",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported games") { Patterns = ["*.gba", "*.nds", "*.demogame.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner is null)
        {
            return null;
        }

        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        if (Owner is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, confirmText, destructive);
        return await dialog.ShowDialog<bool>(Owner);
    }
}
