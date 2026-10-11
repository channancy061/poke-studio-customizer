using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Validation;

namespace PokeStudio.App.ViewModels;

public enum NavKey
{
    Overview, Species, Moves, Learnsets, Evolutions, Trainers, TrainerAi, Graphics, Models3d,
    Encounters, Items, Text, Randomizer, Build, Backups, Settings, About, Modernize, Progression, ChampionLab, GameOptions, EvolutionFixes, Guide,
}

public sealed class NavItemViewModel(NavKey key, string label, string icon, bool enabled, string? reason, string section)
{
    public NavKey Key { get; } = key;
    public string Label { get; } = label;
    public string Section { get; } = section;
    public Geometry? Icon { get; } = ShellViewModel.FindIcon(enabled ? icon : "Icon.Lock");
    public bool IsEnabled { get; } = enabled;
    public bool IsLocked => !IsEnabled;
    public string? Reason { get; } = reason;
    public bool HasReason => !string.IsNullOrEmpty(Reason);
    public string AccessibleName => IsLocked ? $"{Label}, locked: {Reason}" : Label;
}

public sealed partial class IssueViewModel(ValidationIssue issue, ShellViewModel shell) : ViewModelBase
{
    public bool IsError => issue.Severity == IssueSeverity.Error;
    public bool IsWarning => !IsError;
    public string Badge => IsError ? "Must fix" : "Check this";
    public string Text => $"{issue.Subject} {issue.Message}";
    public string? Suggestion => issue.Suggestion;
    public bool HasSuggestion => !string.IsNullOrEmpty(issue.Suggestion);
    public bool HasFix => issue.Fix is not null;
    public bool CanGo => issue.TargetKey is not null;

    [RelayCommand]
    private void Fix() => shell.ApplyFix(issue);

    [RelayCommand]
    private void Go() => shell.GoToIssue(issue);
}

public sealed partial class ChangeItemViewModel(IChange change, ShellViewModel shell) : ViewModelBase
{
    public string Description => change.Description;
    public string Area => change.Area;

    [RelayCommand]
    private void Undo() => shell.UndoSingle(change);
}

/// <summary>The workspace: top bar, capability-driven side nav, current page, changes panel, status bar.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private static readonly (NavKey Key, ModuleCapabilities Cap, string Label, string Icon)[] EditScreens =
    [
        (NavKey.Species, ModuleCapabilities.Species, "Pokémon", "Icon.Creature"),
        (NavKey.Moves, ModuleCapabilities.Moves, "Moves", "Icon.Moves"),
        (NavKey.Learnsets, ModuleCapabilities.Learnsets, "Learnsets", "Icon.List"),
        (NavKey.Evolutions, ModuleCapabilities.Evolutions, "Evolutions", "Icon.ArrowRight"),
        (NavKey.Trainers, ModuleCapabilities.Trainers, "Trainers", "Icon.Trainer"),
        (NavKey.TrainerAi, ModuleCapabilities.TrainerAi, "Enemy AI", "Icon.Ai"),
        (NavKey.Graphics, ModuleCapabilities.Graphics, "Graphics", "Icon.Palette"),
        (NavKey.Models3d, ModuleCapabilities.Models3d, "3D models", "Icon.Cube"),
        (NavKey.Encounters, ModuleCapabilities.Encounters, "Wild encounters", "Icon.Map"),
        (NavKey.Items, ModuleCapabilities.ItemsAndMarts, "Items & shops", "Icon.Bag"),
        (NavKey.Text, ModuleCapabilities.Text, "Text", "Icon.Text"),
    ];

    private readonly MainWindowViewModel _main;

    public ShellViewModel(ProjectSession session, MainWindowViewModel main)
    {
        Session = session;
        _main = main;
        Search = new GlobalSearchViewModel(this);
        BuildNav();
        OriginalStats = session.Data.Species.ToDictionary(s => s.Key, s => s.Stats, StringComparer.Ordinal);
        session.Undo.Changed += (_, _) => { RefreshChanges(); DataChanged?.Invoke(this, EventArgs.Empty); };
        session.DataReloaded += (_, _) => { RefreshChanges(); Navigate(SelectedNav?.Key ?? NavKey.Overview); };
        RefreshChanges();
        SelectedNav = NavItems.FirstOrDefault(n => n.Key == NavKey.Species && n.IsEnabled) ?? NavItems[0];
        if (!main.Settings.Current.TourCompleted)
        {
            StartTour();
        }
    }

    public ProjectSession Session { get; }

    /// <summary>Raised after any edit, undo or redo so the open page can refresh. Cleared when the page changes.</summary>
    public event EventHandler? DataChanged;

    /// <summary>The Pokémon picked on any of the Pokémon / Learnset / Evolution pages (kept when switching).</summary>
    public string? SelectedSpeciesKey { get; set; }

    /// <summary>The move picked on the Moves page.</summary>
    public string? SelectedMoveKey { get; set; }

    /// <summary>The trainer picked on the Trainers page.</summary>
    public string? SelectedTrainerKey { get; set; }

    public string? SelectedEncounterKey { get; set; }
    public string? SelectedItemKey { get; set; }
    public string? SelectedMartKey { get; set; }
    public string? SelectedTextKey { get; set; }

    public GlobalSearchViewModel Search { get; }

    /// <summary>Base stats when the project was opened, for the "before" shape on the radar chart.</summary>
    public IReadOnlyDictionary<string, BaseStats> OriginalStats { get; }

    public ObservableCollection<IssueViewModel> Issues { get; } = [];

    [ObservableProperty]
    public partial bool IsValidationOpen { get; set; }

    public bool HasErrors => Issues.Any(i => i.IsError);
    public bool CanSaveAnyway => !HasErrors;
    public string ValidationTitle => Issues.Count == 0 ? "All clear" : $"{Issues.Count} thing{(Issues.Count == 1 ? "" : "s")} to look at before saving";
    public string ValidationFooter => HasErrors ? "Fix the “Must fix” items first. “Check this” items won't stop you saving." : "Nothing blocks saving.";
    internal MainWindowViewModel Main => _main;
    public string ProjectName => Session.Project.Manifest.Name;
    public string GameTitle => Session.Project.Manifest.GameTitle;
    public string PlatformLabel => Session.Module.Platform.ToString().ToUpperInvariant();

    public ObservableCollection<NavItemViewModel> NavItems { get; } = [];
    public ObservableCollection<ChangeItemViewModel> Changes { get; } = [];

    /// <summary>Features this game can never support: hidden from the menu, counted so it's not a mystery.</summary>
    public int HiddenCount { get; private set; }

    public string HiddenText => HiddenCount == 1 ? "1 tool hidden for this game" : $"{HiddenCount} tools hidden for this game";
    public bool HasHidden => HiddenCount > 0;

    [ObservableProperty]
    public partial NavItemViewModel? SelectedNav { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    [ObservableProperty]
    public partial bool IsChangesOpen { get; set; } = true;

    [ObservableProperty]
    public partial string? Toast { get; set; }

    public int UnsavedCount => Session.Undo.UnsavedCount;
    public bool IsDirty => Session.Undo.IsDirty;
    public string UnsavedText => IsDirty ? $"{UnsavedCount} unsaved change{(UnsavedCount == 1 ? "" : "s")}" : "All changes saved";
    public string BackupText => Session.LastBackupUtc is { } t ? "Backup " + Ago(t) : "No backup yet";
    public string UndoTip => Session.Undo.NextUndo is { } c ? "Undo: " + c.Description + " (Ctrl+Z)" : "Nothing to undo";
    public string RedoTip => Session.Undo.NextRedo is { } c ? "Redo: " + c.Description + " (Ctrl+Y)" : "Nothing to redo";
    public bool HasChanges => Changes.Count > 0;
    public bool AdvancedMode => _main.Settings.Current.AdvancedMode;

    internal static Geometry? FindIcon(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null;

    private void BuildNav()
    {
        var described = Session.Module.DescribeFeatures().ToDictionary(f => f.Feature);
        NavItems.Add(new NavItemViewModel(NavKey.Overview, "Overview", "Icon.Home", true, null, "Project"));
        var hidden = 0;
        foreach (var (key, cap, label, icon) in EditScreens)
        {
            var support = described.TryGetValue(cap, out var f)
                ? f
                : new FeatureSupport(cap, Session.Module.Capabilities.HasFlag(cap) ? SupportLevel.Supported : SupportLevel.Never);
            switch (support.Level)
            {
                case SupportLevel.Supported or SupportLevel.Limited:
                    NavItems.Add(new NavItemViewModel(key, label, icon, true, support.Reason, "Edit"));
                    break;
                case SupportLevel.NotYet:
                    NavItems.Add(new NavItemViewModel(key, label, icon, false, support.Reason ?? "Not available for this game yet.", "Edit"));
                    break;
                default:
                    hidden++;
                    break;
            }
        }

        HiddenCount = hidden;
        if (Session.Data.Trainers.Count > 0)
        {
            NavItems.Add(new NavItemViewModel(NavKey.ChampionLab, "Champion Lab", "Icon.Shield", true, null, "Tools"));
        }

        if (Session.Data.Evolutions.Any(s => s.LockedReason is null && s.Evolutions.Count > 0))
        {
            NavItems.Add(new NavItemViewModel(NavKey.EvolutionFixes, "Fix evolutions", "Icon.Sparkle", true, null, "Tools"));
        }

        if (Session.Module.SupportsGameOptions(Session.Data))
        {
            NavItems.Add(new NavItemViewModel(NavKey.GameOptions, "Game options", "Icon.Settings", true, null, "Tools"));
        }

        if (Session.Data.Rules is { } rules && (rules.LevelCaps.Count > 0 || rules.PhysicalSpecialSplit is not null))
        {
            NavItems.Add(new NavItemViewModel(NavKey.Progression, "Levels & caps", "Icon.ChevronUp", true, null, "Tools"));
        }

        if (Session.Module.ModernizeGenerations(Session.Data).Count > 0)
        {
            NavItems.Add(new NavItemViewModel(NavKey.Modernize, "Modernize", "Icon.Sparkle", true, null, "Tools"));
        }

        if (Session.Data.Species.Count > 0)
        {
            NavItems.Add(new NavItemViewModel(NavKey.Randomizer, "Randomizer", "Icon.Shuffle", true, null, "Tools"));
        }

        NavItems.Add(new NavItemViewModel(NavKey.Guide, "Player's Guide", "Icon.Text", true, null, "Tools"));
        NavItems.Add(new NavItemViewModel(NavKey.Build, "Build & test", "Icon.Build", true, null, "Tools"));
        NavItems.Add(new NavItemViewModel(NavKey.Backups, "Backups", "Icon.Archive", true, null, "Tools"));
        NavItems.Add(new NavItemViewModel(NavKey.Settings, "Settings", "Icon.Settings", true, null, "App"));
        NavItems.Add(new NavItemViewModel(NavKey.About, "Help & about", "Icon.Help", true, null, "App"));
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is not null)
        {
            Navigate(value.Key);
        }
    }

    public void Navigate(NavKey key)
    {
        var item = NavItems.FirstOrDefault(n => n.Key == key);
        DataChanged = null; // the old page stops listening
        CurrentPage = key switch
        {
            NavKey.Overview => new OverviewPageViewModel(this),
            NavKey.Species => new PokemonPageViewModel(this),
            NavKey.Moves when item is { IsEnabled: true } => new MovesPageViewModel(this),
            NavKey.Trainers when item is { IsEnabled: true } => new TrainersPageViewModel(this),
            NavKey.TrainerAi when item is { IsEnabled: true } => new EnemyAiPageViewModel(this),
            NavKey.Modernize => new ModernizePageViewModel(this),
            NavKey.Progression => new ProgressionPageViewModel(this),
            NavKey.ChampionLab => new ChampionLabPageViewModel(this),
            NavKey.GameOptions => new GameOptionsPageViewModel(this),
            NavKey.EvolutionFixes => new EvolutionFixPageViewModel(this),
            NavKey.Guide => new GuidePageViewModel(this),
            NavKey.Randomizer => new RandomizerPageViewModel(this),
            NavKey.Graphics when item is { IsEnabled: true } && Session.Module is PokeStudio.Core.Graphics.ISpriteSource => new GraphicsPageViewModel(this),
            NavKey.Encounters when item is { IsEnabled: true } => new EncountersPageViewModel(this),
            NavKey.Items when item is { IsEnabled: true } => new ItemsPageViewModel(this),
            NavKey.Text when item is { IsEnabled: true } => new TextPageViewModel(this),
            NavKey.Learnsets when item is { IsEnabled: true } => new LearnsetPageViewModel(this),
            NavKey.Evolutions when item is { IsEnabled: true } => new EvolutionPageViewModel(this),
            NavKey.Build => new BuildPageViewModel(this),
            NavKey.Backups => new BackupsPageViewModel(this),
            NavKey.Settings => new SettingsPageViewModel(_main),
            NavKey.About => new AboutPageViewModel(StartTour),
            _ when item is { IsLocked: true } => new PlaceholderPageViewModel(item.Label, item.Reason ?? "", locked: true),
            _ => new PlaceholderPageViewModel(item?.Label ?? key.ToString(), "This editor arrives in a later phase.", locked: false),
        };
    }

    internal void Execute(IChange change) => Session.Undo.Execute(change);

    internal void Select(NavKey key)
    {
        var item = NavItems.First(n => n.Key == key);
        if (SelectedNav == item)
        {
            Navigate(key); // same page: rebuild so the new selection shows
        }
        else
        {
            SelectedNav = item;
        }
    }

    /// <summary>Opens a Pokémon, staying on the Learnset or Evolution page if that's where the user is.</summary>
    internal void GoToSpecies(string key)
    {
        SelectedSpeciesKey = key;
        var page = SelectedNav?.Key is NavKey.Learnsets or NavKey.Evolutions ? SelectedNav.Key : NavKey.Species;
        Select(page);
    }

    internal void GoToMove(string key)
    {
        SelectedMoveKey = key;
        Select(NavKey.Moves);
    }

    internal void GoToTrainer(string key)
    {
        SelectedTrainerKey = key;
        Select(NavKey.Trainers);
    }

    /// <summary>Top bar "Build": opens Build &amp; test and starts a build when the tools are ready.</summary>
    [RelayCommand]
    private async Task BuildGameAsync()
    {
        Select(NavKey.Build);
        if (CurrentPage is BuildPageViewModel page && page.CanBuildGame)
        {
            if (!page.Tools.HasChecked)
            {
                await page.Tools.CheckAsync();
            }

            if (page.BuildCommand.CanExecute(null))
            {
                await page.BuildCommand.ExecuteAsync(null);
            }
        }
    }

    /// <summary>Top bar "Test my game": plays the latest build, or explains what's needed.</summary>
    [RelayCommand]
    private void TestGame()
    {
        var latest = Core.Build.BuildService.History(Session.Project.Directory).FirstOrDefault();
        var platform = Session.Module.Platform == GamePlatform.Demo ? GamePlatform.Gba : Session.Module.Platform;
        if (latest is null)
        {
            Select(NavKey.Build);
            Toast = "Build your game first, or pick a game file to test on this page.";
            return;
        }

        var (ok, message) = _main.LaunchGame(platform, latest.FilePath);
        if (!ok)
        {
            Select(NavKey.Build);
        }

        Toast = message;
    }

    /// <summary>Validates and saves before a build. False when something blocks saving (the check panel opens).</summary>
    internal async Task<bool> SaveForBuildAsync()
    {
        if (!RunValidation() && HasErrors)
        {
            IsValidationOpen = true;
            return false;
        }

        await SaveNowAsync();
        return !IsDirty;
    }

    // ---- Guided tour (first launch, replayable from Help & about) ----

    private static readonly (string Title, string Text)[] TourSteps =
    [
        ("Pick what to edit", "This menu lists everything you can change in this game. Locked items say why; tools a game can never support are hidden and counted at the bottom."),
        ("Make your changes here", "Pick a Pokémon and change anything: stats, types, moves, evolutions. Problems are explained in plain words as you go."),
        ("Every change is listed", "Each edit appears here. Undo any one of them on its own, or press Ctrl+Z. Nothing touches your files until you save, and a backup is made first."),
        ("Build and play", "“Build” turns your project into a game file. “Test my game” opens it in your emulator. Set them up any time under Build & test."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTourOpen), nameof(IsTourNav), nameof(IsTourPage), nameof(IsTourChanges), nameof(IsTourActions),
        nameof(TourTitle), nameof(TourText), nameof(TourStepText), nameof(TourIsLast), nameof(TourIsFirst), nameof(TourNextText),
        nameof(TourHorizontal), nameof(TourVertical), nameof(TourMargin))]
    public partial int TourStep { get; set; } = -1;

    public bool IsTourOpen => TourStep >= 0;
    public bool IsTourNav => TourStep == 0;
    public bool IsTourPage => TourStep == 1;
    public bool IsTourChanges => TourStep == 2;
    public bool IsTourActions => TourStep == 3;
    public bool TourIsFirst => TourStep <= 0;
    public bool TourIsLast => TourStep == TourSteps.Length - 1;
    public string TourNextText => TourIsLast ? "Got it" : "Next";
    public string TourTitle => IsTourOpen ? TourSteps[TourStep].Title : "";
    public string TourText => IsTourOpen ? TourSteps[TourStep].Text : "";
    public string TourStepText => IsTourOpen ? $"{TourStep + 1} of {TourSteps.Length}" : "";

    public Avalonia.Layout.HorizontalAlignment TourHorizontal => TourStep switch
    {
        0 => Avalonia.Layout.HorizontalAlignment.Left,
        2 or 3 => Avalonia.Layout.HorizontalAlignment.Right,
        _ => Avalonia.Layout.HorizontalAlignment.Center,
    };

    public Avalonia.Layout.VerticalAlignment TourVertical => TourStep == 3 ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;

    public Thickness TourMargin => TourStep switch
    {
        0 => new Thickness(250, 0, 0, 0),
        2 => new Thickness(0, 0, 340, 0),
        3 => new Thickness(0, 72, 16, 0),
        _ => new Thickness(0),
    };

    public void StartTour()
    {
        var first = NavItems.Any(n => n.Key == NavKey.Species && n.IsEnabled) ? NavKey.Species : NavKey.Overview;
        if (SelectedNav?.Key != first)
        {
            Select(first);
        }

        TourStep = 0;
    }

    [RelayCommand]
    private async Task TourNextAsync()
    {
        if (TourIsLast)
        {
            await EndTourAsync();
            return;
        }

        TourStep++;
        if (IsTourChanges)
        {
            IsChangesOpen = true;
        }
    }

    [RelayCommand]
    private void TourBack()
    {
        if (TourStep > 0)
        {
            TourStep--;
        }
    }

    [RelayCommand]
    private async Task EndTourAsync()
    {
        TourStep = -1;
        await _main.Settings.UpdateAsync(s => s with { TourCompleted = true });
    }

    internal void UndoSingle(IChange change) => Session.Undo.UndoSingle(change);

    [RelayCommand]
    private void Undo() => Session.Undo.Undo();

    [RelayCommand]
    private void Redo() => Session.Undo.Redo();

    /// <summary>Checks everything first; problems open the check panel instead of saving.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!RunValidation())
        {
            IsValidationOpen = true;
            return;
        }

        await SaveNowAsync();
    }

    [RelayCommand]
    private async Task SaveAnywayAsync()
    {
        RunValidation();
        if (HasErrors)
        {
            return;
        }

        IsValidationOpen = false;
        await SaveNowAsync();
    }

    [RelayCommand]
    private void CloseValidation() => IsValidationOpen = false;

    private async Task SaveNowAsync()
    {
        try
        {
            await Session.SaveAsync();
            RefreshChanges();
            Toast = "Saved.";
        }
        // (InvalidOperationException: a module explaining why it can't write, e.g. no free flags left for the game options)
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Safety.UnsafeWriteException or InvalidOperationException)
        {
            Toast = "Couldn't save: " + ex.Message;
        }
    }

    /// <returns>True when there is nothing to show.</returns>
    internal bool RunValidation()
    {
        Issues.Clear();
        foreach (var issue in Session.Validate().OrderBy(i => i.Severity))
        {
            Issues.Add(new IssueViewModel(issue, this));
        }

        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(CanSaveAnyway));
        OnPropertyChanged(nameof(ValidationTitle));
        OnPropertyChanged(nameof(ValidationFooter));
        return Issues.Count == 0;
    }

    internal void ApplyFix(ValidationIssue issue)
    {
        if (issue.Fix is { } fix)
        {
            Session.Undo.Execute(fix(Session.Data));
            RunValidation();
        }
    }

    internal void GoToIssue(ValidationIssue issue)
    {
        IsValidationOpen = false;
        var key = issue.TargetKey;
        if (issue.Area == "Learnset")
        {
            key = Session.Data.Learnsets.FirstOrDefault(l => l.Key == key)?.UsedBy.FirstOrDefault() ?? key;
        }

        if (issue.Area == "Move" && key is not null)
        {
            GoToMove(key);
            return;
        }

        if (issue.Area == "Trainer" && key is not null)
        {
            GoToTrainer(key);
            return;
        }

        switch (issue.Area)
        {
            case "Encounter":
                SelectedEncounterKey = key;
                Select(NavKey.Encounters);
                return;
            case "Item":
                SelectedItemKey = key;
                SelectedMartKey = null;
                Select(NavKey.Items);
                return;
            case "Mart":
                SelectedMartKey = key;
                SelectedItemKey = null;
                Select(NavKey.Items);
                return;
            case "Text":
                SelectedTextKey = key;
                Select(NavKey.Text);
                return;
            case "Level caps":
                Select(NavKey.Progression);
                return;
        }

        SelectedSpeciesKey = key;
        var nav = issue.Area switch { "Learnset" => NavKey.Learnsets, "Evolution" => NavKey.Evolutions, _ => NavKey.Species };
        SelectedNav = NavItems.FirstOrDefault(n => n.Key == nav && n.IsEnabled) ?? SelectedNav;
    }

    [RelayCommand]
    internal async Task BackupNowAsync()
    {
        await Session.BackupAsync(BackupReason.Manual, keep: _main.Settings.Current.BackupKeepCount);
        RefreshChanges();
        Toast = "Backup saved.";
    }

    [RelayCommand]
    private void ToggleChanges() => IsChangesOpen = !IsChangesOpen;

    [RelayCommand]
    private async Task GoHomeAsync()
    {
        if (Session.Undo.IsDirty && !await _main.Dialogs.ConfirmAsync(
                $"Leave without saving?",
                $"You have {UnsavedText.ToLowerInvariant()}. They're still in your last backup if you leave now.",
                "Leave without saving",
                destructive: true))
        {
            return;
        }

        _main.GoHome();
    }

    [RelayCommand]
    private void NotYet(string what) => Toast = $"{what} arrives in a later phase.";

    internal void RefreshChanges()
    {
        Changes.Clear();
        foreach (var change in Session.Undo.History.Reverse())
        {
            Changes.Add(new ChangeItemViewModel(change, this));
        }

        OnPropertyChanged(nameof(UnsavedCount));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(UnsavedText));
        OnPropertyChanged(nameof(BackupText));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        OnPropertyChanged(nameof(HasChanges));
    }

    private string Ago(DateTimeOffset t)
    {
        var span = TimeProvider.System.GetUtcNow() - t;
        return span.TotalMinutes < 1 ? "saved just now"
            : span.TotalHours < 1 ? $"saved {(int)span.TotalMinutes} min ago"
            : "saved " + t.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }
}
