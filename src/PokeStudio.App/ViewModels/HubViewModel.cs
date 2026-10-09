using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Settings;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.App.ViewModels;

public sealed partial class HubViewModel : ViewModelBase, IDisposable
{
    private readonly MainWindowViewModel _main;

    public HubViewModel(MainWindowViewModel main)
    {
        _main = main;
        Refresh();
    }

    public ObservableCollection<RecentProjectViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;
    public bool IsEmpty => Recent.Count == 0;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public static string DemoGamePath => Path.Combine(AppContext.BaseDirectory, "samples", "Demo Valley.demogame.json");

    public void Refresh()
    {
        Recent.Clear();
        foreach (var r in _main.Settings.Current.RecentProjects.Where(r => Directory.Exists(r.Path)))
        {
            Recent.Add(new RecentProjectViewModel(r, this));
        }

        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task OpenGameFileAsync()
    {
        var path = await _main.Dialogs.PickGameFileAsync();
        if (path is not null)
        {
            await RunAsync(() => _main.CreateProjectFromGameAsync(path));
        }
    }

    [RelayCommand]
    private async Task OpenProjectFolderAsync()
    {
        var folder = await _main.Dialogs.PickFolderAsync("Open a project folder");
        if (folder is not null)
        {
            await RunAsync(() => _main.OpenProjectFolderAsync(folder));
        }
    }

    [RelayCommand]
    private async Task OpenDecompProjectAsync()
    {
        var folder = await _main.Dialogs.PickFolderAsync("Open your pokeemerald-expansion folder");
        if (folder is not null)
        {
            await RunAsync(() => _main.CreateProjectFromGameAsync(folder));
        }
    }

    private CancellationTokenSource? _download;

    public void Dispose() => _download?.Dispose();

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial string? DownloadText { get; set; }

    [ObservableProperty]
    public partial double DownloadPercent { get; set; }

    [ObservableProperty]
    public partial bool DownloadIndeterminate { get; set; }

    public static string ExpansionRelease => "pokeemerald-expansion " + ExpansionDownloader.ReleaseTag.Replace("expansion/", "", StringComparison.Ordinal);

    [RelayCommand]
    private async Task DownloadExpansionAsync()
    {
        var parent = await _main.Dialogs.PickFolderAsync("Where should pokeemerald-expansion go?");
        if (parent is null)
        {
            return;
        }

        _download = new CancellationTokenSource();
        IsDownloading = true;
        DownloadText = "Starting download…";
        DownloadIndeterminate = true;
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadIndeterminate = p.Fraction is null;
            DownloadPercent = (p.Fraction ?? 0) * 100;
            DownloadText = $"{p.Stage} · {p.Detail}";
        });
        try
        {
            await RunAsync(() => _main.DownloadExpansionAsync(parent, progress, _download.Token));
        }
        catch (OperationCanceledException)
        {
            Message = "Download cancelled. Nothing was kept.";
        }
        finally
        {
            IsDownloading = false;
            _download.Dispose();
            _download = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private Task TryDemoAsync() => RunAsync(() => _main.CreateProjectFromGameAsync(DemoGamePath));

    internal Task OpenRecentAsync(RecentProject project) => RunAsync(() => _main.OpenProjectFolderAsync(project.Path));

    private async Task RunAsync(Func<Task<string?>> action)
    {
        Message = null;
        IsBusy = true;
        try
        {
            Message = await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Message = "Something went wrong: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class RecentProjectViewModel(RecentProject project, HubViewModel hub) : ViewModelBase
{
    public string Name => project.Name;
    public string PlatformLabel => project.Platform.ToString().ToUpperInvariant();
    public string Folder => project.Path;
    public string LastOpened => "Opened " + project.LastOpenedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    [RelayCommand]
    private Task OpenAsync() => hub.OpenRecentAsync(project);
}
