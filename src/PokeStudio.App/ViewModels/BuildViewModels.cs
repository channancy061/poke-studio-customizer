using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Build;
using PokeStudio.Core.Models;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.ViewModels;

public sealed partial class ToolRowViewModel(ToolStatus status) : ViewModelBase
{
    public string Name { get; } = status.Name;
    public string Detail { get; } = status.Detail;
    public string? HowToFix { get; } = status.HowToFix;
    public bool HasHowToFix => !string.IsNullOrEmpty(HowToFix);
    public bool IsReady { get; } = status.State == ToolState.Ready;
    public bool IsWarning { get; } = status.State == ToolState.Warning;
    public bool IsMissing { get; } = status.State == ToolState.Missing;
    public ToolFix Fix { get; } = status.Fix;
    public bool CanAutoFix => Fix == ToolFix.InstallBuildTools;
    public string Badge => IsReady ? "Ready" : IsWarning ? "Check" : "Missing";
}

/// <summary>Build-tool status with "Check again" and, where the app may do it, an install button. Shared by the setup wizard and the build page.</summary>
public sealed partial class ToolsPanelViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public ToolsPanelViewModel(MainWindowViewModel main, BuildDefinition? build)
    {
        _main = main;
        Build = build;
    }

    public BuildDefinition? Build { get; }
    public ObservableCollection<ToolRowViewModel> Rows { get; } = [];
    public string EnvironmentName => _main.Builds.Environment.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial string InstallLog { get; set; } = "";

    public bool HasChecked { get; private set; }
    public bool AllReady => HasChecked && Rows.Count > 0 && Rows.All(r => !r.IsMissing);

    /// <summary>True on Windows when WSL itself is the missing piece (option B: everything else still works).</summary>
    public bool NeedsWsl => Rows.Any(r => r.Fix is ToolFix.InstallWsl or ToolFix.InstallUbuntu);

    public string Summary => IsChecking ? "Checking…"
        : !HasChecked ? ""
        : AllReady ? "Everything needed to build is installed."
        : NeedsWsl ? "Building needs WSL. Everything else in the app works without it."
        : "Some build tools are missing.";

    public event EventHandler? Checked;

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (Build is null || IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var result = await _main.Builds.CheckAsync(Build);
            Rows.Clear();
            foreach (var s in result)
            {
                Rows.Add(new ToolRowViewModel(s));
            }
        }
        catch (Exception ex) when (ex is ProgramNotFoundException or IOException or InvalidOperationException)
        {
            Rows.Clear();
            Rows.Add(new ToolRowViewModel(new ToolStatus("Build tools", ToolState.Missing, "We couldn't check: " + ex.Message)));
        }
        finally
        {
            HasChecked = true;
            IsChecking = false;
            OnPropertyChanged(nameof(AllReady));
            OnPropertyChanged(nameof(NeedsWsl));
            OnPropertyChanged(nameof(Summary));
            Checked?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task InstallAsync(ToolRowViewModel row)
    {
        if (Build is null || _main.Builds.Environment.FixCommand(row.Fix, Build) is not { } command)
        {
            return;
        }

        var ok = await _main.Dialogs.ConfirmAsync(
            $"Install the {Build.ToolsDescription}?",
            $"The app installs them inside {_main.Builds.Environment.Name} only (about 1 GB download, a few minutes). Nothing outside it changes.",
            "Install");
        if (!ok)
        {
            return;
        }

        IsInstalling = true;
        var log = new StringBuilder();
        var progress = new Progress<string>(line =>
        {
            log.AppendLine(line);
            InstallLog = Tail(log.ToString(), 30);
        });
        try
        {
            var result = await _main.Runner.RunAsync(command, line => ((IProgress<string>)progress).Report(line));
            InstallLog = result.Succeeded ? "Installed." : "The install didn't finish. " + Tail(result.Output, 8);
        }
        catch (ProgramNotFoundException ex)
        {
            InstallLog = ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }

        await CheckAsync();
    }

    internal static string Tail(string text, int lines)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}

/// <summary>One platform's emulator: the path, a Test button, and emulators found on this computer.</summary>
public sealed partial class EmulatorSlotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public EmulatorSlotViewModel(MainWindowViewModel main, GamePlatform platform)
    {
        _main = main;
        Platform = platform;
        _path = main.Settings.Current.EmulatorPaths.GetValueOrDefault(platform);
        var s = Emulators.Suggested(platform);
        Suggestion = s is null ? "" : $"We suggest {s.Name} — free and open-source. {s.Note} Get it from {s.Website}.";
        Refresh();
    }

    public GamePlatform Platform { get; }
    public string Label => Emulators.Label(Platform) + " games";
    public string Suggestion { get; }
    public ObservableCollection<EmulatorCandidate> Found { get; } = [];
    public bool HasFound => Found.Count > 0;

    private string? _path;

    public string? Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"')))
            {
                var platform = Platform;
                var path = _path;
                _ = _main.Settings.UpdateAsync(s =>
                {
                    var map = new Dictionary<GamePlatform, string>(s.EmulatorPaths);
                    if (path is null)
                    {
                        map.Remove(platform);
                    }
                    else
                    {
                        map[platform] = path;
                    }

                    return s with { EmulatorPaths = map };
                });
                Refresh();
            }
        }
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial ToolState State { get; set; }

    public bool IsReady => State == ToolState.Ready;
    public bool IsWarning => State == ToolState.Warning;
    public bool IsMissing => State == ToolState.Missing;
    public bool HasPath => _path is not null;

    internal void AddFound(EmulatorCandidate candidate)
    {
        if (Found.All(f => !f.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Found.Add(candidate);
            OnPropertyChanged(nameof(HasFound));
        }
    }

    private void Refresh()
    {
        var check = Emulators.Check(_path, Platform);
        Status = check.Message;
        State = check.State;
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(HasPath));
    }

    [RelayCommand]
    private void Use(EmulatorCandidate candidate) => Path = candidate.Path;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var file = await _main.Dialogs.PickFileAsync($"Pick your {Emulators.Label(Platform)} emulator",
            [("Programs", OperatingSystem.IsWindows() ? ["*.exe"] : ["*"])]);
        if (file is not null)
        {
            Path = file;
        }
    }

    /// <summary>Checks the file and opens the emulator so the user sees it start.</summary>
    [RelayCommand]
    private void Test()
    {
        Refresh();
        if (_path is null || State == ToolState.Missing)
        {
            return;
        }

        try
        {
            _main.Runner.Launch(new ProcessSpec(_path, [], System.IO.Path.GetDirectoryName(_path)));
            Status += " It should open in a moment — close it when you've seen it.";
        }
        catch (ProgramNotFoundException)
        {
            Status = "This program couldn't be started. Try picking it again.";
            State = ToolState.Missing;
        }
    }

    [RelayCommand]
    private void Clear() => Path = null;
}

/// <summary>First-launch setup: theme, emulators, build tools, backups. Can be run again from Settings.</summary>
public sealed partial class SetupWizardViewModel : ViewModelBase
{
    public static readonly string[] StepTitles = ["Welcome", "Emulators", "Build tools", "Backups"];
    private readonly MainWindowViewModel _main;

    public SetupWizardViewModel(MainWindowViewModel main, bool searchNow = true)
    {
        _main = main;
        Slots = [new(main, GamePlatform.Gba), new(main, GamePlatform.Nds), new(main, GamePlatform.N3ds), new(main, GamePlatform.Gb)];
        Tools = new ToolsPanelViewModel(main, main.ToolsBuildDefinition);
        if (searchNow)
        {
            _ = SearchAsync();
        }
    }

    public IReadOnlyList<EmulatorSlotViewModel> Slots { get; }
    public ToolsPanelViewModel Tools { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsEmulators), nameof(IsTools), nameof(IsBackups), nameof(IsLast), nameof(IsFirst), nameof(StepText), nameof(StepTitle))]
    public partial int Step { get; set; }

    public bool IsWelcome => Step == 0;
    public bool IsEmulators => Step == 1;
    public bool IsTools => Step == 2;
    public bool IsBackups => Step == 3;
    public bool IsFirst => Step == 0;
    public bool IsLast => Step == StepTitles.Length - 1;
    public string StepText => $"Step {Step + 1} of {StepTitles.Length}";
    public string StepTitle => StepTitles[Step];
    public bool IsWindows => OperatingSystem.IsWindows();

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string SearchStatus { get; set; } = "";

    private AppSettings S => _main.Settings.Current;

    public bool IsLight { get => S.Theme == ThemePreference.Light; set { if (value) { SetTheme(ThemePreference.Light); } } }
    public bool IsDark { get => S.Theme == ThemePreference.Dark; set { if (value) { SetTheme(ThemePreference.Dark); } } }
    public bool IsSystem { get => S.Theme == ThemePreference.System; set { if (value) { SetTheme(ThemePreference.System); } } }

    public string BackupsFolder => _main.BackupsFolder;

    public decimal BackupKeepCount
    {
        get => S.BackupKeepCount;
        set => Update(s => s with { BackupKeepCount = (int)Math.Clamp(value, 1, 500) });
    }

    public decimal BuildsKeepCount
    {
        get => S.BuildsKeepCount;
        set => Update(s => s with { BuildsKeepCount = (int)Math.Clamp(value, 1, 50) });
    }

    private void SetTheme(ThemePreference theme)
    {
        _main.Theme.Apply(theme);
        Update(s => s with { Theme = theme });
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        _ = _main.Settings.UpdateAsync(change);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Looks in the usual places for emulators the user already has.</summary>
    [RelayCommand]
    public async Task SearchAsync() => await SearchAsync(Emulators.DefaultSearchRoots());

    internal async Task SearchAsync(IReadOnlyList<string> roots)
    {
        if (IsSearching)
        {
            return;
        }

        IsSearching = true;
        SearchStatus = "Looking for emulators on this computer…";
        try
        {
            var found = await Task.Run(() => Emulators.Find(roots));
            foreach (var c in found)
            {
                foreach (var slot in Slots.Where(s => c.Kind.Platforms.Contains(s.Platform)))
                {
                    slot.AddFound(c);
                }
            }

            SearchStatus = found.Count == 0
                ? "We didn't find any emulators. Pick one with “Browse…”, or install the suggested one."
                : $"Found {found.Count} emulator{(found.Count == 1 ? "" : "s")}. Choose “Use this” for the ones you want.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (!IsLast)
        {
            Step++;
            if (IsTools && !Tools.HasChecked)
            {
                await Tools.CheckAsync();
            }
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst)
        {
            Step--;
        }
    }

    [RelayCommand]
    private async Task ChangeBackupsFolderAsync()
    {
        var folder = await _main.Dialogs.PickFolderAsync("Where should backups go?");
        if (folder is not null)
        {
            Update(s => s with { BackupsFolder = folder });
        }
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        await _main.Settings.UpdateAsync(s => s with { FirstRunCompleted = true });
        _main.SetupWizard = null;
    }
}

public sealed partial class BuildRowViewModel(BuildRecord record, BuildPageViewModel page, bool latest) : ViewModelBase
{
    public BuildRecord Record { get; } = record;
    public string Name => Record.FileName;
    public string When => Record.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public string Size => $"{Record.Size / 1024.0 / 1024.0:0.0} MB";
    public bool IsLatest { get; } = latest;

    [RelayCommand]
    private void Test() => page.Play(Record.FilePath);

    [RelayCommand]
    private void ShowInFolder() => page.ShowInFolder(Record.FilePath);
}

public sealed record ProblemRowViewModel(string Title, string Explanation, string? Location, string RawLine)
{
    public bool HasLocation => Location is not null;
}

/// <summary>Build my game + Test my game, for the open project.</summary>
public sealed partial class BuildPageViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogLines = 400;
    private readonly ShellViewModel _shell;
    private readonly Queue<string> _log = new();
    private CancellationTokenSource? _cts;

    public BuildPageViewModel(ShellViewModel shell, bool checkNow = true)
    {
        _shell = shell;
        Definition = shell.Session.Module.BuildFor(shell.Session.Project.WorkingSourcePath);
        Tools = new ToolsPanelViewModel(shell.Main, Definition);
        Tools.Checked += (_, _) => { OnPropertyChanged(nameof(BuildBlockedReason)); OnPropertyChanged(nameof(IsBuildBlocked)); BuildCommand.NotifyCanExecuteChanged(); };
        Emulator = new EmulatorSlotViewModel(shell.Main, shell.Session.Module.Platform == GamePlatform.Demo ? GamePlatform.Gba : shell.Session.Module.Platform);
        ShowLog = shell.Main.Settings.Current.AdvancedMode;
        LoadHistory();
        if (checkNow && Definition is not null)
        {
            _ = Tools.CheckAsync();
        }
    }

    public BuildDefinition? Definition { get; }
    public bool CanBuildGame => Definition is not null;

    /// <summary>A project made from a ROM file: no build step, the edited copy is played directly.</summary>
    public bool IsRomProject => Definition is null && File.Exists(_shell.Session.Project.WorkingSourcePath)
        && Path.GetExtension(_shell.Session.Project.WorkingSourcePath) is ".gba" or ".nds" or ".3ds" or ".gbc" or ".gb";
    public bool CannotBuildGame => !CanBuildGame;
    public ToolsPanelViewModel Tools { get; }
    public EmulatorSlotViewModel Emulator { get; }
    public ObservableCollection<BuildRowViewModel> History { get; } = [];
    public ObservableCollection<ProblemRowViewModel> Problems { get; } = [];
    public bool HasHistory => History.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public string KeepText => $"The last {_shell.Main.Settings.Current.BuildsKeepCount} builds are kept, each with the date in its name.";
    public string OutputFolder => BuildService.BuildsDir(_shell.Session.Project.Directory);
    public string WslHelp => WslBuildEnvironment.WslInstructions;

    public string CannotBuildText => IsRomProject
        ? $"This project edits a copy of your ROM, so there's nothing to build: save your changes, then \"Test my game\" plays the edited copy. The copy is in {Path.GetDirectoryName(_shell.Session.Project.WorkingSourcePath)} (your original file is never changed)."
        : $"{_shell.Session.Module.DisplayName} projects can't be built into a game yet. You can still test any game file you have with your emulator.";

    public bool IsBuildBlocked => CanBuildGame && Tools.HasChecked && !Tools.AllReady;

    public string BuildBlockedReason => Tools.NeedsWsl
        ? "Building needs WSL (Windows Subsystem for Linux). pokeemerald-expansion's build tools don't run on plain Windows, so Windows runs them inside WSL. Everything else in the app works without it."
        : "Some build tools are missing — see the list below.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CancelCommand))]
    public partial bool IsBuilding { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Step { get; set; } = "";

    [ObservableProperty]
    public partial string Log { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowLog { get; set; }

    [ObservableProperty]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial bool ResultIsSuccess { get; set; }

    [ObservableProperty]
    public partial string? PlayMessage { get; set; }

    [ObservableProperty]
    public partial bool PlayMessageIsWarning { get; set; }

    public bool HasResultError => Result is not null && !ResultIsSuccess;

    partial void OnResultChanged(string? value) => OnPropertyChanged(nameof(HasResultError));

    partial void OnResultIsSuccessChanged(bool value) => OnPropertyChanged(nameof(HasResultError));

    private bool CanBuild() => CanBuildGame && !IsBuilding && Tools.AllReady;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    public async Task BuildAsync()
    {
        if (Definition is null)
        {
            return;
        }

        if (_shell.IsDirty)
        {
            var save = await _shell.Main.Dialogs.ConfirmAsync("Save your changes first?",
                "The game is built from your saved files, so unsaved changes wouldn't be in it.", "Save and build");
            if (!save || !await _shell.SaveForBuildAsync())
            {
                return;
            }
        }

        IsBuilding = true;
        Result = null;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        _log.Clear();
        Log = "";
        Progress = 0;
        _cts = new CancellationTokenSource();
        try
        {
            var settings = _shell.Main.Settings.Current;
            if (settings.BackupBeforeBuild)
            {
                Step = "Making a backup…";
                await _shell.Session.BackupAsync(BackupReason.BeforeBuild, keep: settings.BackupKeepCount);
            }

            var progress = new Progress<BuildProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Step = p.Step;
                if (p.LogLine is { } line)
                {
                    AppendLog(line);
                }
            });
            var result = await _shell.Main.Builds.BuildAsync(Definition, _shell.Session.Project.WorkingSourcePath,
                _shell.Session.Project.Directory, settings.BuildsKeepCount, progress, _cts.Token);
            ShowResult(result);
        }
        finally
        {
            IsBuilding = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    internal void ShowResult(BuildResult result)
    {
        Problems.Clear();
        switch (result.Outcome)
        {
            case BuildOutcome.Succeeded:
                ResultIsSuccess = true;
                Result = $"Your game is ready: {result.Output!.FileName}";
                Progress = 100;
                Step = "Done!";
                LoadHistory();
                break;
            case BuildOutcome.Cancelled:
                ResultIsSuccess = false;
                Result = "Build stopped. Nothing was changed.";
                Step = "";
                break;
            case BuildOutcome.NotReady:
                ResultIsSuccess = false;
                Result = BuildBlockedReason;
                Step = "";
                _ = Tools.CheckAsync();
                break;
            default:
                ResultIsSuccess = false;
                Result = result.Problems.Count == 1 ? "The build stopped because of 1 problem." : $"The build stopped because of {result.Problems.Count} problems.";
                Step = "";
                foreach (var p in result.Problems.Take(20))
                {
                    Problems.Add(new ProblemRowViewModel(p.Title, p.Explanation, p.File is null ? null : $"{p.File}{(p.Line is { } l ? ":" + l : "")}", p.RawLine));
                }

                if (Log.Length == 0)
                {
                    Log = ToolsPanelViewModel.Tail(result.Log, MaxLogLines);
                }

                break;
        }

        OnPropertyChanged(nameof(HasProblems));
    }

    private bool CanCancel() => IsBuilding;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ToggleLog() => ShowLog = !ShowLog;

    [RelayCommand]
    private void TestLatest()
    {
        if (History.FirstOrDefault() is { } latest)
        {
            Play(latest.Record.FilePath);
        }
        else if (IsRomProject)
        {
            // ROM projects edit a copy of the game: that copy is the game to play (save first to include changes)
            Play(_shell.Session.Project.WorkingSourcePath);
        }
        else
        {
            SetPlayMessage("There's no build yet. Build your game first, or pick a game file to test.", warning: true);
        }
    }

    [RelayCommand]
    private async Task TestFileAsync()
    {
        var ext = _shell.Session.Module.Platform switch { GamePlatform.Nds => "*.nds", GamePlatform.N3ds => "*.3ds", GamePlatform.Gb => "*.gbc", _ => "*.gba" };
        var file = await _shell.Main.Dialogs.PickFileAsync("Pick a game file to test", [("Game files", [ext])]);
        if (file is not null)
        {
            Play(file);
        }
    }

    internal void Play(string gameFile)
    {
        var (ok, message) = _shell.Main.LaunchGame(Emulator.Platform, gameFile);
        SetPlayMessage(message, warning: !ok || Emulator.IsWarning);
    }

    private void SetPlayMessage(string message, bool warning)
    {
        PlayMessage = message;
        PlayMessageIsWarning = warning;
    }

    internal void ShowInFolder(string file) => _shell.Main.ShowInFolder(file);

    [RelayCommand]
    private void OpenBuildsFolder()
    {
        Directory.CreateDirectory(OutputFolder);
        _shell.Main.ShowInFolder(OutputFolder);
    }

    private void LoadHistory()
    {
        History.Clear();
        var first = true;
        foreach (var r in BuildService.History(_shell.Session.Project.Directory))
        {
            History.Add(new BuildRowViewModel(r, this, first));
            first = false;
        }

        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>Leaving the page doesn't stop a running build; closing the project does.</summary>
    public void Dispose() => _cts?.Cancel();

    private void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
        {
            _log.Dequeue();
        }

        Log = string.Join('\n', _log);
    }
}
