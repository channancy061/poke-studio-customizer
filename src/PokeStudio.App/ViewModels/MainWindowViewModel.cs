using CommunityToolkit.Mvvm.ComponentModel;
using PokeStudio.App.Services;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

/// <summary>Top-level navigation: the project hub, or the workspace shell for an open project.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ModuleRegistry _modules;
    private readonly ProjectService _projects;
    private readonly BackupService _backups;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _clock;
    private ProjectSession? _session;

    public MainWindowViewModel(
        ModuleRegistry modules,
        ProjectService projects,
        BackupService backups,
        SettingsStore settings,
        ThemeService theme,
        IDialogService dialogs,
        IAppPaths paths,
        TimeProvider clock)
    {
        _modules = modules;
        _projects = projects;
        _backups = backups;
        _paths = paths;
        _clock = clock;
        Settings = settings;
        Theme = theme;
        Dialogs = dialogs;
        Hub = new HubViewModel(this);
        CurrentView = Hub;
        Settings.Changed += (_, _) => TextScale = Settings.Current.TextScale;
        TextScale = Settings.Current.TextScale;
    }

    public SettingsStore Settings { get; }
    public ThemeService Theme { get; }
    public IDialogService Dialogs { get; }
    public HubViewModel Hub { get; }
    public ModuleRegistry Modules => _modules;

    [ObservableProperty]
    public partial ViewModelBase CurrentView { get; set; }

    /// <summary>Whole-UI zoom from Settings › Text size.</summary>
    [ObservableProperty]
    public partial double TextScale { get; set; }

    public string ProjectsFolder => Settings.Current.ProjectsFolder ?? _paths.DefaultProjectsDir;

    /// <summary>Detects the game, copies it into a new project and opens it.</summary>
    public async Task<string?> CreateProjectFromGameAsync(string path)
    {
        var source = new GameSource(path, Directory.Exists(path) ? GameSourceKind.DecompProject : GameSourceKind.RomFile);
        var match = _modules.Detect(source);
        if (match is null)
        {
            return "We don't recognise this file yet. In this version you can open the Demo Valley sample; real games arrive in the next phases.";
        }

        var (module, result) = match.Value;
        var name = string.IsNullOrWhiteSpace(result.GameTitle) ? Path.GetFileNameWithoutExtension(path) : result.GameTitle;
        var project = await _projects.CreateAsync(name, source with { Kind = result.SourceKind }, module, result, ProjectsFolder);
        await OpenProjectAsync(project);
        return null;
    }

    public async Task<string?> OpenProjectFolderAsync(string folder)
    {
        try
        {
            await OpenProjectAsync(await _projects.OpenAsync(folder));
            return null;
        }
        catch (ProjectFormatException ex)
        {
            return ex.Message;
        }
        catch (IOException ex)
        {
            return "The project couldn't be opened: " + ex.Message;
        }
    }

    public async Task OpenProjectAsync(Project project)
    {
        _session?.Dispose();
        _session = await ProjectSession.OpenAsync(project, _modules, _projects, _backups, _clock);
        await Settings.UpdateAsync(s => s.WithRecent(new RecentProject(project.Directory, project.Manifest.Name, project.Manifest.Platform, _clock.GetUtcNow())));
        CurrentView = new ShellViewModel(_session, this);
    }

    public void GoHome()
    {
        _session?.Dispose();
        _session = null;
        Hub.Refresh();
        CurrentView = Hub;
    }

    public void Dispose() => _session?.Dispose();
}
