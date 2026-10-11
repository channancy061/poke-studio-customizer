using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.ViewModels;

public sealed record FeatureRow(string Name, string Status, string? Reason, bool IsOk, bool IsLimited, bool IsOff);

public sealed class OverviewPageViewModel : ViewModelBase
{
    public OverviewPageViewModel(ShellViewModel shell)
    {
        var m = shell.Session.Project.Manifest;
        Title = m.Name;
        Subtitle = $"{m.GameTitle} · {shell.Session.Module.DisplayName}";
        ProjectFolder = shell.Session.Project.Directory;
        Original = m.OriginalSourcePath;
        Features = shell.Session.Module.DescribeFeatures()
            .Select(f => new FeatureRow(
                f.Feature.ToString(),
                f.Level switch { SupportLevel.Supported => "Ready", SupportLevel.Limited => "Limited", SupportLevel.NotYet => "Not yet", _ => "Not possible" },
                f.Reason,
                f.Level == SupportLevel.Supported,
                f.Level == SupportLevel.Limited,
                f.Level is SupportLevel.NotYet or SupportLevel.Never))
            .ToList();
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string ProjectFolder { get; }
    public string Original { get; }
    public IReadOnlyList<FeatureRow> Features { get; }
}

public sealed partial class PlaceholderPageViewModel(string title, string message, bool locked) : ViewModelBase
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public bool IsLocked { get; } = locked;
    public bool IsComing { get; } = !locked;
}

public sealed partial class BackupRowViewModel(BackupManifest manifest, BackupsPageViewModel page) : ViewModelBase
{
    public BackupManifest Manifest { get; } = manifest;
    public string Title => Manifest.Reason switch
    {
        BackupReason.Manual => "Manual backup",
        BackupReason.Auto => "Auto-save",
        BackupReason.BeforeBuild => "Before build",
        BackupReason.BeforeRestore => "Before restore",
        BackupReason.BeforeRandomize => "Before randomizer",
        _ => "Backup",
    };

    public string When => Manifest.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public string Details => $"{Manifest.Files.Count} files · {Manifest.TotalBytes / 1024.0:0.#} KB" + (Manifest.Label is { } l ? " · " + l : "");
    public bool Pinned => Manifest.Pinned;
    public string PinText => Pinned ? "Unpin" : "Pin";

    [RelayCommand]
    private Task RestoreAsync() => page.RestoreAsync(this);

    [RelayCommand]
    private Task TogglePinAsync() => page.SetPinnedAsync(this, !Pinned);
}

public sealed partial class BackupsPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public BackupsPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _ = LoadAsync();
    }

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool IsEmpty => Backups.Count == 0;

    public async Task LoadAsync()
    {
        Backups.Clear();
        foreach (var b in await _shell.Session.ListBackupsAsync())
        {
            Backups.Add(new BackupRowViewModel(b, this));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        await _shell.BackupNowAsync();
        await LoadAsync();
    }

    internal async Task SetPinnedAsync(BackupRowViewModel row, bool pinned)
    {
        await _shell.Session.SetBackupPinnedAsync(row.Manifest.Id, pinned);
        await LoadAsync();
    }

    internal async Task RestoreAsync(BackupRowViewModel row)
    {
        var ok = await _shell.Main.Dialogs.ConfirmAsync(
            $"Restore “{row.Title}” from {row.When}?",
            "We'll back up what you have now first, so you can come back to it. Unsaved changes and the undo list are cleared.",
            "Restore");
        if (!ok)
        {
            return;
        }

        try
        {
            await _shell.RunBusyHereAsync("Restoring the backup…", () => _shell.Session.RestoreAsync(row.Manifest.Id));
            Message = "Restored. A safety backup of your previous state was made first.";
        }
        catch (BackupCorruptException ex)
        {
            Message = "This backup can't be used: " + ex.Message + " Nothing was changed.";
        }

        await LoadAsync();
    }
}

public sealed partial class SettingsPageViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public SettingsPageViewModel(MainWindowViewModel main)
    {
        _main = main;
        Emulators = [new(main, GamePlatform.Gba), new(main, GamePlatform.Nds), new(main, GamePlatform.N3ds), new(main, GamePlatform.Gb)];
    }

    public IReadOnlyList<EmulatorSlotViewModel> Emulators { get; }

    public string BackupsFolder => _main.BackupsFolder;

    public decimal BuildsKeepCount
    {
        get => S.BuildsKeepCount;
        set => Update(s => s with { BuildsKeepCount = (int)Math.Clamp(value, 1, 50) });
    }

    [RelayCommand]
    private void RunSetup() => _main.ShowSetup();

    [RelayCommand]
    private async Task ChangeBackupsFolderAsync()
    {
        var folder = await _main.Dialogs.PickFolderAsync("Where should backups go?");
        if (folder is not null)
        {
            Update(s => s with { BackupsFolder = folder });
        }
    }

    private AppSettings S => _main.Settings.Current;

    public IReadOnlyList<string> TextSizes { get; } = ["Small", "Default", "Large", "Larger"];
    private static readonly double[] Scales = [0.9, 1.0, 1.15, 1.3];

    public bool IsLight { get => S.Theme == ThemePreference.Light; set { if (value) { SetTheme(ThemePreference.Light); } } }
    public bool IsDark { get => S.Theme == ThemePreference.Dark; set { if (value) { SetTheme(ThemePreference.Dark); } } }
    public bool IsSystem { get => S.Theme == ThemePreference.System; set { if (value) { SetTheme(ThemePreference.System); } } }

    public int TextSizeIndex
    {
        get => Math.Max(0, Array.FindIndex(Scales, x => Math.Abs(x - S.TextScale) < 0.01));
        set => Update(s => s with { TextScale = Scales[Math.Clamp(value, 0, Scales.Length - 1)] });
    }

    public bool AdvancedMode { get => S.AdvancedMode; set => Update(s => s with { AdvancedMode = value }); }
    public bool ReduceMotion { get => S.ReduceMotion; set => Update(s => s with { ReduceMotion = value }); }

    public decimal BackupKeepCount
    {
        get => S.BackupKeepCount;
        set => Update(s => s with { BackupKeepCount = (int)Math.Clamp(value, 1, 500) });
    }

    public bool BackupBeforeBuild { get => S.BackupBeforeBuild; set => Update(s => s with { BackupBeforeBuild = value }); }
    public bool GuideWithBuild { get => S.GuideWithBuild; set => Update(s => s with { GuideWithBuild = value }); }
    public string ProjectsFolder => _main.ProjectsFolder;
    public string SettingsFile => "Settings are stored in your user profile's app-data folder.";

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

    [RelayCommand]
    private async Task ChangeProjectsFolderAsync()
    {
        var folder = await _main.Dialogs.PickFolderAsync("Where should new projects go?");
        if (folder is not null)
        {
            Update(s => s with { ProjectsFolder = folder });
        }
    }
}

public sealed record LicenseRow(string Name, string License, string Use);

public sealed partial class AboutPageViewModel(Action? replayTour = null) : ViewModelBase
{
    public bool CanReplayTour => replayTour is not null;

    [RelayCommand]
    private void ReplayTour() => replayTour?.Invoke();

    public string Name => AppInfo.DisplayName;
    public string Version => "Version " + AppInfo.Version + " · Windows & Linux";

    public IReadOnlyList<LicenseRow> Licenses { get; } =
    [
        new("Avalonia UI", "MIT", "Cross-platform interface"),
        new(".NET runtime", "MIT", "App runtime (bundled)"),
        new("SkiaSharp / HarfBuzzSharp", "MIT", "Drawing and text shaping (via Avalonia)"),
        new("CommunityToolkit.Mvvm", "MIT", "App structure"),
        new("Microsoft.Extensions.DependencyInjection", "MIT", "App structure"),
        new("Microsoft.Data.Sqlite", "MIT", "Change history"),
        new("SQLitePCLRaw", "Apache-2.0", "SQLite bindings"),
        new("SQLite", "Public domain", "Change history database engine"),
        new("Figtree", "SIL OFL 1.1", "Interface font"),
        new("Silkscreen", "SIL OFL 1.1", "Pixel accent font"),
        new("JetBrains Mono", "SIL OFL 1.1", "Logs and technical values"),
        new("Icons", "Original (Lucide-style, ISC-compatible)", "Interface icons"),
    ];
}
