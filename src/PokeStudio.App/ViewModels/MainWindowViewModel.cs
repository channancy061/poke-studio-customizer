using CommunityToolkit.Mvvm.ComponentModel;
using PokeStudio.App.Services;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Build;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;
using PokeStudio.Modules.Expansion;

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
        TimeProvider clock,
        ExpansionDownloader downloader,
        BuildService builds,
        IProcessRunner runner)
    {
        Builds = builds;
        Runner = runner;
        Downloader = downloader;
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
        Settings.Changed += (_, _) => ApplySettings();
        ApplySettings();
    }

    public SettingsStore Settings { get; }
    public ThemeService Theme { get; }
    public IDialogService Dialogs { get; }
    public HubViewModel Hub { get; }
    public ModuleRegistry Modules => _modules;
    public ExpansionDownloader Downloader { get; }
    public BuildService Builds { get; }
    public IProcessRunner Runner { get; }

    /// <summary>The setup wizard, shown over everything while open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupOpen))]
    public partial SetupWizardViewModel? SetupWizard { get; set; }

    public bool IsSetupOpen => SetupWizard is not null;

    /// <summary>The build the setup wizard checks tools for (the first module that can build).</summary>
    public BuildDefinition? ToolsBuildDefinition => _modules.Modules.Select(m => m.Build).FirstOrDefault(b => b is not null);

    public string BackupsFolder => Settings.Current.BackupsFolder ?? _paths.BackupsDir;

    public void ShowSetup(bool searchNow = true) => SetupWizard = new SetupWizardViewModel(this, searchNow);

    private void ApplySettings()
    {
        TextScale = Settings.Current.TextScale;
        _backups.Root = BackupsFolder;
    }

    /// <summary>Opens <paramref name="gameFile"/> in the emulator set for <paramref name="platform"/>.</summary>
    /// <returns>Whether it started, and a plain-English message.</returns>
    public (bool Ok, string Message) LaunchGame(GamePlatform platform, string gameFile)
    {
        var path = Settings.Current.EmulatorPaths.GetValueOrDefault(platform);
        var check = Emulators.Check(path, platform);
        if (path is null || check.State == ToolState.Missing)
        {
            return (false, $"Set up a {Emulators.Label(platform)} emulator first. {check.Message}");
        }

        if (!File.Exists(gameFile))
        {
            return (false, "That game file doesn't exist any more.");
        }

        try
        {
            Runner.Launch(Emulators.LaunchSpec(path, gameFile));
        }
        catch (ProgramNotFoundException)
        {
            return (false, "The emulator couldn't be started. Pick it again in Settings › Emulators.");
        }

        var name = check.Kind?.Name ?? Path.GetFileNameWithoutExtension(path);
        return (true, check.Kind is { NoteIsWarning: true } k
            ? $"Opening {Path.GetFileName(gameFile)} in {name}. Note: {k.Note}"
            : $"Opening {Path.GetFileName(gameFile)} in {name}.");
    }

    /// <summary>Opens a document (e.g. the Player's Guide) in the program the computer uses for it.</summary>
    public void OpenFile(string path)
    {
        try
        {
            Runner.Launch(OperatingSystem.IsWindows() ? new ProcessSpec("explorer.exe", [path]) : new ProcessSpec("xdg-open", [path]));
        }
        catch (ProgramNotFoundException)
        {
            // nothing can open it: the file is still there
        }
    }

    public void ShowInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Runner.Launch(File.Exists(path) ? new ProcessSpec("explorer.exe", ["/select," + path]) : new ProcessSpec("explorer.exe", [path]));
            }
            else
            {
                Runner.Launch(new ProcessSpec("xdg-open", [File.Exists(path) ? Path.GetDirectoryName(path)! : path]));
            }
        }
        catch (ProgramNotFoundException)
        {
            // no file manager: nothing to do
        }
    }

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
            return Directory.Exists(path)
                ? "This folder isn't a pokeemerald-expansion project. Pick the folder that contains the Makefile, or download the project from the hub."
                : "We don't recognise this file yet. Game files (ROMs) arrive in a later phase; for now open a pokeemerald-expansion project or try the demo.";
        }

        var (module, result) = match.Value;
        var name = result.SourceKind == GameSourceKind.DecompProject
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : string.IsNullOrWhiteSpace(result.GameTitle) ? Path.GetFileNameWithoutExtension(path) : result.GameTitle;
        var project = await _projects.CreateAsync(name, source with { Kind = result.SourceKind }, module, result, ProjectsFolder);
        await OpenProjectAsync(project);
        return null;
    }

    /// <summary>Downloads pokeemerald-expansion into <paramref name="parent"/>, then opens it as a project.</summary>
    public async Task<string?> DownloadExpansionAsync(string parent, IProgress<DownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        string folder;
        try
        {
            folder = await Downloader.DownloadAsync(parent, progress, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return "The download didn't work. Check your internet connection and try again. (" + ex.Message + ")";
        }

        return await CreateProjectFromGameAsync(folder);
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

    public void Dispose()
    {
        _session?.Dispose();
        Hub.Dispose();
    }
}
